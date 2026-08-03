using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

using System.Buffers.Binary;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §22.1–22.4 (array population, large arrays,
    /// sparse/wasteful arrays, jagged vs multi-dimensional analysis).
    ///
    /// Population aggregate (§22.1) filters <c>TypeAggregates</c> by
    /// <see cref="TypeAggregateFlags.IsArrayType"/> — no heap scan for basic totals.
    ///
    /// Large array analysis (§22.2) reads <c>LargeObjectIndex.bin</c> (top-100 LOH objects)
    /// and cross-references with array MTs; falls back to TypeAggregates for memory mode.
    ///
    /// Sparse sampling (§22.3) is bounded to <see cref="SparseSampleLimit"/> arrays
    /// with at least <see cref="SparseSampleMinLength"/> elements; samples every
    /// <see cref="SampleStride"/>-th element to estimate null/zero density.
    /// </summary>
    public sealed class ArrayAnalyzer : IAnalyzer
    {
        public string Name => "Array Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArrayAnalysisOptions options = context.AnalysisOptions.ArrayAnalysis;
            return ValueTask.FromResult(
                Analyze(context.Heap, context.Cache, context.Progress, options, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrHeap heap,
            IHeapAnalysisCache cache,
            IProgress<AnalyzerProgressReport>? progress,
            ArrayAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            HeapIndexBuildResult? heapIndex = null;

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out heapIndex))
                typeAggregates = heapIndex.TypeAggregates;

            if (typeAggregates is null)
                return new ArrayDomainResult(0, 0, 0, 0, 0, [], [], [], false);

            // ── Step 1: Aggregate population from TypeAggregates ─────────────────
            progress?.Report(new(0, "scanning array type aggregates"));

            // Build set of array MTs for fast lookup
            var arrayMtSet = new HashSet<ulong>(capacity: 512);
            // Map: elementTypeName+rank key → (count, totalBytes, isMultiDim)
            var typeMap = new Dictionary<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank)>(256);

            long totalObjects = 0;
            ulong totalBytes = 0;
            int multiDimCount = 0;
            int lohCount = 0;
            ulong lohBytes = 0;

            // Sparse candidates collected in a single pass to avoid a second typeAggregates scan.
            // Only 1-D reference-type arrays with a valid SampleAddress are eligible.
            // Sorted by TotalSize descending before sampling so the largest arrays are probed first.
            var sparseCandidates = new List<(ulong SampleAddress, string ElemName, ulong TotalSize)>(64);

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                TypeAggregateIndexEntry e = kv.Value;
                if ((e.Flags & TypeAggregateFlags.IsArrayType) == 0)
                    continue;

                arrayMtSet.Add(kv.Key);

                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType is null) continue;

                string elemName = clrType.ComponentType?.Name ?? clrType.Name ?? $"MT:0x{kv.Key:X}";
                int rank = 1;
                bool isMultiDim = false;

                // Detect multi-dimensional arrays from the type name (e.g. "System.Int32[,]")
                if (clrType.Name is string name)
                {
                    int bracket = name.LastIndexOf('[');
                    if (bracket >= 0)
                    {
                        int commas = 0;
                        for (int ci = bracket; ci < name.Length; ci++)
                            if (name[ci] == ',') commas++;
                        if (commas > 0)
                        {
                            rank = commas + 1;
                            isMultiDim = true;
                        }
                    }
                }

                totalObjects += e.Count;
                totalBytes += e.TotalSize;
                if (isMultiDim) multiDimCount += (int)Math.Min(e.Count, int.MaxValue);
                if (e.LohCount > 0)
                {
                    lohCount += (int)Math.Min(e.LohCount, int.MaxValue);
                    lohBytes += e.LohSize;
                }

                string key = $"{elemName}[rank={rank}]";
                if (typeMap.TryGetValue(key, out var existing))
                    typeMap[key] = (existing.Count + e.Count,
                                    existing.Bytes + e.TotalSize, isMultiDim, rank);
                else
                    typeMap[key] = (e.Count, e.TotalSize, isMultiDim, rank);

                // Sparse candidate: only 1-D ref-type arrays with a sample address.
                // GetObjectValue is only valid for reference-type elements — value-type arrays
                // are excluded entirely to avoid exception-driven null-counting.
                if (!isMultiDim && rank == 1
                    && clrType.ComponentType?.IsObjectReference == true
                    && e.SampleAddress != 0)
                {
                    sparseCandidates.Add((e.SampleAddress, elemName, e.TotalSize));
                }
            }

            // ── Step 2: Top array types by total bytes ────────────────────────────
            var typeList = new List<(string ElemName, int Rank, int Count, ulong Bytes, bool IsMultiDim)>(typeMap.Count);
            foreach (KeyValuePair<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank)> kv in typeMap)
            {
                // Extract element name from key (strip "[rank=N]" suffix)
                int rankSep = kv.Key.LastIndexOf("[rank=", StringComparison.Ordinal);
                string elemName = rankSep > 0 ? kv.Key[..rankSep] : kv.Key;
                typeList.Add((elemName, kv.Value.Rank, (int)Math.Min(kv.Value.Count, int.MaxValue), kv.Value.Bytes, kv.Value.IsMultiDim));
            }
            typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

            int typeListLimit = Math.Min(typeList.Count, options.TopTypeLimit);
            var topArrayTypes = new List<ArrayTypeProfile>(typeListLimit);
            for (int i = 0; i < typeListLimit; i++)
            {
                var t = typeList[i];
                topArrayTypes.Add(new ArrayTypeProfile(t.ElemName, t.Rank, t.Count, t.Bytes, t.IsMultiDim));
            }

            // ── Step 3: Large array analysis ──────────────────────────────────────
            // Try LargeObjectIndex.bin (disk mode) first; fall back to TypeAggregates LohSize
            progress?.Report(new(0, "analysing large arrays"));

            var topLargeArrays = new List<LargeArrayEntry>(options.TopLargeLimit);

            if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk)
            {
                ReadLargeArraysFromIndex(heap, heapIndex.IndexPath, arrayMtSet, topLargeArrays, options.TopLargeLimit, cancellationToken);
            }

            // If index wasn't available, use SampleAddress from TypeAggregates for top LOH array types
            if (topLargeArrays.Count == 0)
            {
                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
                {
                    if ((kv.Value.Flags & TypeAggregateFlags.IsArrayType) == 0) continue;
                    if (kv.Value.LohCount == 0 || kv.Value.SampleAddress == 0) continue;

                    ClrObject obj = heap.GetObject(kv.Value.SampleAddress);
                    if (!obj.IsValid || obj.Type is null) continue;

                    string elemName = obj.Type.ComponentType?.Name ?? obj.Type.Name ?? "Unknown";
                    ClrArray arr = obj.AsArray();
                    topLargeArrays.Add(new LargeArrayEntry(
                        Address: kv.Value.SampleAddress,
                        ElementTypeName: elemName,
                        Length: arr.Length,
                        Rank: arr.Rank,
                        Size: obj.Size));

                    if (topLargeArrays.Count >= options.TopLargeLimit) break;
                }
            }

            // ── Step 4: Sparse sampling ───────────────────────────────────────────
            // Use pre-collected ref-type candidates from Step 1 — no second typeAggregates scan.
            // Sort by TotalSize descending so the most impactful arrays are probed first.
            // Cap at SparseSampleLimit before opening any objects.
            progress?.Report(new(0, "sampling sparse arrays"));

            sparseCandidates.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));

            bool scanLimited = sparseCandidates.Count > options.SparseSampleLimit;
            int candidateLimit = Math.Min(sparseCandidates.Count, options.SparseSampleLimit);

            var topSparseArrays = new List<SparseArrayEntry>(options.TopSparseLimit);

            for (int ci = 0; ci < candidateLimit && topSparseArrays.Count < options.TopSparseLimit; ci++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (sampleAddr, elemName, _) = sparseCandidates[ci];

                ClrObject obj = heap.GetObject(sampleAddr);
                if (!obj.IsValid || obj.Type is null) continue;

                ClrArray arr = obj.AsArray();
                if (arr.Length < options.SparseSampleMinLength) continue;
                // Rank was already confirmed == 1 at collection time, but re-check for safety
                if (arr.Rank > 1) continue;

                int nullCount = 0;
                int sampleLen = 0;
                for (int i = 0; i < arr.Length; i += options.SampleStride)
                {
                    ClrObject elem = arr.GetObjectValue(i);
                    if (!elem.IsValid || elem.Address == 0) nullCount++;
                    sampleLen++;
                }

                if (sampleLen == 0) continue;

                double sparseRatio = (double)nullCount / sampleLen;
                if (sparseRatio < 0.5) continue;

                ulong elemSize = (ulong)IntPtr.Size;
                ulong wastedBytes = (ulong)(arr.Length * sparseRatio * (double)elemSize);

                topSparseArrays.Add(new SparseArrayEntry(
                    Address: sampleAddr,
                    ElementTypeName: elemName,
                    Length: arr.Length,
                    NullOrZeroCount: (int)Math.Min((long)(nullCount * ((double)arr.Length / sampleLen)), int.MaxValue),
                    SparseRatio: sparseRatio,
                    WastedBytes: wastedBytes));
            }

            topSparseArrays.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

            return new ArrayDomainResult(
                TotalArrayObjects: (int)Math.Min(totalObjects, int.MaxValue),
                TotalArrayBytes: totalBytes,
                MultiDimArrayCount: multiDimCount,
                LohArrayCount: lohCount,
                LohArrayBytes: lohBytes,
                TopArrayTypesBySize: topArrayTypes,
                TopLargeArrays: topLargeArrays,
                TopSparseArrays: topSparseArrays,
                ScanLimited: scanLimited);
        }

        // ── Large array index reader ──────────────────────────────────────────────

        private static void ReadLargeArraysFromIndex(
            ClrHeap heap,
            string containerPath,
            HashSet<ulong> arrayMtSet,
            List<LargeArrayEntry> result,
            int topLargeLimit,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.LargeObjects, out Stream? stream) || stream is null)
                    return;

                using (stream)
                {
                    if (!IndexHeader.TryRead(stream, out IndexHeader header))
                        return;

                    const int RecordSize = 24; // Address(8) | MT(8) | Size(8)
                    Span<byte> rec = stackalloc byte[RecordSize];
                    for (long i = 0; i < header.RecordCount && result.Count < topLargeLimit; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                        if (read < RecordSize) break;

                        ulong address = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                        ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);

                        if (!arrayMtSet.Contains(mt)) continue;

                        ClrObject obj = heap.GetObject(address);
                        if (!obj.IsValid || obj.Type is null) continue;

                        string elemName = obj.Type.ComponentType?.Name ?? obj.Type.Name ?? "Unknown";
                        if (string.Equals(elemName, "Free", StringComparison.Ordinal)) continue;

                        ClrArray arr = obj.AsArray();
                        result.Add(new LargeArrayEntry(
                            Address: address,
                            ElementTypeName: elemName,
                            Length: arr.Length,
                            Rank: arr.Rank,
                            Size: size));
                    }
                }
            }
            catch (Exception)
            {
                // Section not found or read failed; caller will continue without large array index.
            }
        }

        public void Dispose() { }
    }
}
