using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

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
    /// Sparse sampling (§22.3) walks every element of every array with at least
    /// <see cref="ArrayAnalysisOptions.SparseSampleMinLength"/> elements to compute exact
    /// null/zero density.
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
                return new ArrayDomainResult(0, 0, 0, 0, 0, 0, [], [], []);

            // ── Step 1: Aggregate population from TypeAggregates ─────────────────
            progress?.Report(new(0, "scanning array type aggregates"));

            // Build set of array MTs for fast lookup
            var arrayMtSet = new HashSet<ulong>(capacity: 512);
            // Map: elementTypeName+rank key → (count, totalBytes, isMultiDim)
            var typeMap = new Dictionary<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank, long Gen2Count, long LohCount, string ModuleName)>(256);

            long totalObjects = 0;
            ulong totalBytes = 0;
            ulong totalHeapBytes = 0;
            int multiDimCount = 0;
            int lohCount = 0;
            ulong lohBytes = 0;
            ulong multiDimBytes = 0;

            // Sparse candidates collected in a single pass to avoid a second typeAggregates scan.
            // Only 1-D reference-type arrays with a valid SampleAddress are eligible.
            // Sorted by TotalSize descending before sampling so the largest arrays are probed first.
            var sparseCandidates = new List<(ulong SampleAddress, string ElemName, ulong TotalSize)>(64);

            // LOH fallback candidates collected in the same pass to avoid a second typeAggregates
            // scan in Step 3 when the disk-based LargeObjectIndex isn't available. Sorted by
            // LohSize descending before use so the fallback matches the index-based path's ordering.
            var lohFallbackCandidates = new List<(ulong SampleAddress, ulong LohSize)>(64);

            foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
            {
                TypeAggregateIndexEntry e = kv.Value;
                totalHeapBytes += e.TotalSize;

                if ((e.Flags & TypeAggregateFlags.IsArrayType) == 0)
                    continue;

                arrayMtSet.Add(kv.Key);

                ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                if (clrType is null) continue;

                string elemName = clrType.ComponentType?.Name ?? clrType.Name ?? $"MT:0x{kv.Key:X}";
                string moduleName = clrType.Module?.Name ?? "Unknown";
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
                if (isMultiDim) { multiDimCount += (int)Math.Min(e.Count, int.MaxValue); multiDimBytes += e.TotalSize; }
                if (e.LohCount > 0)
                {
                    lohCount += (int)Math.Min(e.LohCount, int.MaxValue);
                    lohBytes += e.LohSize;
                    if (e.SampleAddress != 0)
                        lohFallbackCandidates.Add((e.SampleAddress, e.LohSize));
                }

                string key = $"{elemName}[rank={rank}]";
                if (typeMap.TryGetValue(key, out var existing))
                    typeMap[key] = (existing.Count + e.Count,
                                    existing.Bytes + e.TotalSize, isMultiDim, rank,
                                    existing.Gen2Count + e.Gen2Count, existing.LohCount + e.LohCount, existing.ModuleName);
                else
                    typeMap[key] = (e.Count, e.TotalSize, isMultiDim, rank, e.Gen2Count, e.LohCount, moduleName);

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
            var typeList = new List<(string ElemName, int Rank, int Count, ulong Bytes, bool IsMultiDim, long Gen2Count, long LohCount, string ModuleName)>(typeMap.Count);
            foreach (KeyValuePair<string, (long Count, ulong Bytes, bool IsMultiDim, int Rank, long Gen2Count, long LohCount, string ModuleName)> kv in typeMap)
            {
                // Extract element name from key (strip "[rank=N]" suffix)
                int rankSep = kv.Key.LastIndexOf("[rank=", StringComparison.Ordinal);
                string elemName = rankSep > 0 ? kv.Key[..rankSep] : kv.Key;
                typeList.Add((elemName, kv.Value.Rank, (int)Math.Min(kv.Value.Count, int.MaxValue), kv.Value.Bytes, kv.Value.IsMultiDim, kv.Value.Gen2Count, kv.Value.LohCount, kv.Value.ModuleName));
            }
            typeList.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));

            var topArrayTypes = new List<ArrayTypeProfile>(typeList.Count);
            foreach (var t in typeList)
            {
                double percentOfHeap = totalHeapBytes > 0 ? t.Bytes * 100.0 / totalHeapBytes : 0.0;
                double gen2PlusLohPct = t.Count > 0 ? (t.Gen2Count + t.LohCount) * 100.0 / t.Count : 0.0;
                double avgSize = t.Count > 0 ? t.Bytes / (double)t.Count : 0.0;
                topArrayTypes.Add(new ArrayTypeProfile(t.ElemName, t.Rank, t.Count, t.Bytes, t.IsMultiDim, percentOfHeap, gen2PlusLohPct, avgSize, t.ModuleName));
            }


            // ── Step 3: Large array analysis ──────────────────────────────────────
            // Try LargeObjectIndex.bin (disk mode) first; fall back to TypeAggregates LohSize
            progress?.Report(new(0, "analysing large arrays"));

            var topLargeArrays = new List<LargeArrayEntry>(64);

            if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk)
            {
                LargeObjectTracker.ReadRecords(heapIndex.IndexPath, (address, mt, size) =>
                {
                    if (!arrayMtSet.Contains(mt))
                        return;

                    ClrObject obj = heap.GetObject(address);
                    if (!obj.IsValid || obj.Type is null)
                        return;

                    string elemName = obj.Type.ComponentType?.Name ?? obj.Type.Name ?? "Unknown";
                    if (string.Equals(elemName, "Free", StringComparison.Ordinal))
                        return;

                    ClrArray arr = obj.AsArray();
                    topLargeArrays.Add(new LargeArrayEntry(
                        Address: address,
                        ElementTypeName: elemName,
                        Length: arr.Length,
                        Rank: arr.Rank,
                        Size: size));
                }, cancellationToken);
            }

            // If index wasn't available, use the LOH fallback candidates collected in Step 1
            // (no second typeAggregates pass). Sort by LohSize descending first so the top N
            // taken here are actually the largest LOH arrays, matching the index-based path.
            if (topLargeArrays.Count == 0 && lohFallbackCandidates.Count > 0)
            {
                lohFallbackCandidates.Sort(static (a, b) => b.LohSize.CompareTo(a.LohSize));
                foreach (var candidate in lohFallbackCandidates)
                {
                    ulong sampleAddress = candidate.SampleAddress;
                    ClrObject obj = heap.GetObject(sampleAddress);
                    if (!obj.IsValid || obj.Type is null) continue;

                    string elemName = obj.Type.ComponentType?.Name ?? obj.Type.Name ?? "Unknown";
                    ClrArray arr = obj.AsArray();
                    topLargeArrays.Add(new LargeArrayEntry(
                        Address: sampleAddress,
                        ElementTypeName: elemName,
                        Length: arr.Length,
                        Rank: arr.Rank,
                        Size: obj.Size));
                }
            }
            // Use pre-collected ref-type candidates from Step 1 — no second typeAggregates scan.
            // Every candidate is probed; the sort only affects display/processing order, not which
            // arrays get evaluated.
            progress?.Report(new(0, "sampling sparse arrays"));

            sparseCandidates.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));

            var topSparseArrays = new List<SparseArrayEntry>(64);

            foreach (var (sampleAddr, elemName, _) in sparseCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClrObject obj = heap.GetObject(sampleAddr);
                if (!obj.IsValid || obj.Type is null) continue;

                ClrArray arr = obj.AsArray();
                if (arr.Length < options.SparseSampleMinLength) continue;
                // Rank was already confirmed == 1 at collection time, but re-check for safety
                if (arr.Rank > 1) continue;

                // Walk every element — no stride sampling, so NullOrZeroCount and WastedBytes
                // below are exact counts, not extrapolated estimates.
                int nullCount = 0;
                for (int i = 0; i < arr.Length; i++)
                {
                    ClrObject elem = arr.GetObjectValue(i);
                    if (!elem.IsValid || elem.Address == 0) nullCount++;
                }

                if (arr.Length == 0) continue;

                double sparseRatio = (double)nullCount / arr.Length;
                if (sparseRatio < 0.5) continue;

                ulong elemSize = (ulong)IntPtr.Size;
                ulong wastedBytes = (ulong)nullCount * elemSize;

                topSparseArrays.Add(new SparseArrayEntry(
                    Address: sampleAddr,
                    ElementTypeName: elemName,
                    Length: arr.Length,
                    NullOrZeroCount: nullCount,
                    SparseRatio: sparseRatio,
                    WastedBytes: wastedBytes));
            }

            topSparseArrays.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

            return new ArrayDomainResult(
                TotalArrayObjects: (int)Math.Min(totalObjects, int.MaxValue),
                TotalArrayBytes: totalBytes,
                MultiDimArrayCount: multiDimCount,
                MultiDimArrayBytes: multiDimBytes,
                LohArrayCount: lohCount,
                LohArrayBytes: lohBytes,
                TopArrayTypesBySize: topArrayTypes,
                TopLargeArrays: topLargeArrays,
                TopSparseArrays: topSparseArrays);
        }

        // ── Large array index reader ──────────────────────────────────────────────

        public void Dispose() { }
    }
}
