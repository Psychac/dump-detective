using System.Numerics;

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
    /// Phase-2 analyzer covering §22.1–22.5 (array population, large arrays,
    /// sparse/wasteful arrays, jagged vs multi-dimensional analysis, pinned array detection).
    ///
    /// Population aggregate (§22.1) filters <c>TypeAggregates</c> by
    /// <see cref="TypeAggregateFlags.IsArrayType"/> — no heap scan for basic totals.
    ///
    /// Large array analysis (§22.2) reads <c>LargeObjectIndex.bin</c> (top-100 LOH objects)
    /// and cross-references with array MTs; falls back to TypeAggregates for memory mode.
    ///
    /// Sparse sampling (§22.3) walks every element of every array with at least
    /// <see cref="ArrayAnalysisOptions.SparseSampleMinLength"/> elements to compute exact
    /// null/zero density. Reference-type arrays are walked element-by-element via
    /// <c>GetObjectValue</c>; primitive numeric arrays (<c>int[]</c>, <c>float[]</c>, ...) are
    /// walked via chunked <c>ClrArray.ReadValues&lt;T&gt;</c> bulk reads and counted against
    /// <c>T.Zero</c>.
    ///
    /// Pinned array detection (§22.5) cross-references <see cref="IHeapAnalysisCache.GetPinnedRootedAddresses"/>
    /// (backed by the Phase-1 disk root index) against the array-MT set built in Step 1 — every
    /// pinned array instance is checked exactly, not sampled.
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
            // Eligible: 1-D reference-type arrays, and 1-D primitive-numeric-type arrays, both
            // with a valid SampleAddress. IsReference selects which walk strategy Step 2 uses.
            // Sorted by TotalSize descending before sampling so the largest arrays are probed first.
            int candidateCapacity = Math.Min(typeAggregates.Count / 4, 512);
            var sparseCandidates = new List<(ulong SampleAddress, string ElemName, ulong TotalSize, ClrElementType ElemType, bool IsReference)>(candidateCapacity);

            // LOH fallback candidates collected in the same pass to avoid a second typeAggregates
            // scan in Step 3 when the disk-based LargeObjectIndex isn't available. Sorted by
            // LohSize descending before use so the fallback matches the index-based path's ordering.
            var lohFallbackCandidates = new List<(ulong SampleAddress, ulong LohSize)>(candidateCapacity);

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

                // Sparse candidate: 1-D ref-type arrays (walked via GetObjectValue), or 1-D
                // primitive-numeric-type arrays (walked via chunked ReadValues<T>). Arbitrary
                // structs are excluded — ReadValues<T> requires an exact blittable layout match,
                // which isn't guaranteed for hand-authored struct field ordering, and "zero" is
                // ill-defined for structs holding reference fields.
                bool isReference = clrType.ComponentType?.IsObjectReference == true;
                ClrElementType componentElementType = clrType.ComponentType?.ElementType ?? ClrElementType.Unknown;
                if (!isMultiDim && rank == 1 && e.SampleAddress != 0
                    && (isReference || IsSupportedNumericElementType(componentElementType)))
                {
                    sparseCandidates.Add((e.SampleAddress, elemName, e.TotalSize, componentElementType, isReference));
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
            // Use pre-collected candidates from Step 1 — no second typeAggregates scan.
            // Every candidate is probed; the sort only affects display/processing order, not which
            // arrays get evaluated.
            progress?.Report(new(0, "sampling sparse arrays"));

            sparseCandidates.Sort(static (a, b) => b.TotalSize.CompareTo(a.TotalSize));

            var topSparseArrays = new List<SparseArrayEntry>(64);

            foreach (var (sampleAddr, elemName, _, elemType, isReference) in sparseCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClrObject obj = heap.GetObject(sampleAddr);
                if (!obj.IsValid || obj.Type is null) continue;

                ClrArray arr = obj.AsArray();
                if (arr.Length < options.SparseSampleMinLength) continue;
                // Rank was already confirmed == 1 at collection time, but re-check for safety
                if (arr.Rank > 1) continue;
                if (arr.Length == 0) continue;

                // Walk every element — no stride sampling, so NullOrZeroCount and WastedBytes
                // below are exact counts, not extrapolated estimates. For numeric arrays, a
                // negative result means part of the array was unreadable (e.g. truncated dump);
                // the candidate is skipped rather than reporting a ratio over a partial read.
                int nullOrZeroCount = isReference
                    ? CountNullReferences(arr)
                    : CountZeroElements(arr, arr.Length, elemType);
                if (nullOrZeroCount < 0) continue;

                double sparseRatio = (double)nullOrZeroCount / arr.Length;
                if (sparseRatio < 0.5) continue;

                ulong elemSize = isReference ? (ulong)IntPtr.Size : (ulong)ElementSizeBytes(elemType);
                ulong wastedBytes = (ulong)nullOrZeroCount * elemSize;

                topSparseArrays.Add(new SparseArrayEntry(
                    Address: sampleAddr,
                    ElementTypeName: elemName,
                    Length: arr.Length,
                    NullOrZeroCount: nullOrZeroCount,
                    SparseRatio: sparseRatio,
                    WastedBytes: wastedBytes));
            }

            topSparseArrays.Sort(static (a, b) => b.WastedBytes.CompareTo(a.WastedBytes));

            // ── Step 4: Pinned array detection via GC handle root index ──────────
            // Cross-reference PinnedHandle/AsyncPinnedHandle root targets (read from the Phase-1
            // disk root index — no second heap/handle scan) against arrayMtSet built in Step 1.
            // Pinned handle counts are small (tens-thousands, not heap-sized), so this walk is
            // cheap and — unlike the sparse/LOH candidates above — checks every pinned instance
            // exactly rather than one sample per type.
            progress?.Report(new(0, "detecting pinned arrays"));

            int pinnedArrayCount = 0;
            ulong pinnedArrayBytes = 0;
            var topPinnedArrays = new List<LargeArrayEntry>(64);

            foreach (ulong pinnedAddress in cache.GetPinnedRootedAddresses(heap))
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClrObject obj = heap.GetObject(pinnedAddress);
                if (!obj.IsValid || obj.Type is null) continue;
                if (!arrayMtSet.Contains(obj.Type.MethodTable)) continue;

                ClrArray arr = obj.AsArray();
                string elemName = obj.Type.ComponentType?.Name ?? obj.Type.Name ?? "Unknown";

                pinnedArrayCount++;
                pinnedArrayBytes += obj.Size;
                topPinnedArrays.Add(new LargeArrayEntry(
                    Address: pinnedAddress,
                    ElementTypeName: elemName,
                    Length: arr.Length,
                    Rank: arr.Rank,
                    Size: obj.Size));
            }

            topPinnedArrays.Sort(static (a, b) => b.Size.CompareTo(a.Size));
            if (topPinnedArrays.Count > options.TopPinnedArrayLimit)
                topPinnedArrays.RemoveRange(options.TopPinnedArrayLimit, topPinnedArrays.Count - options.TopPinnedArrayLimit);

            return new ArrayDomainResult(
                TotalArrayObjects: (int)Math.Min(totalObjects, int.MaxValue),
                TotalArrayBytes: totalBytes,
                MultiDimArrayCount: multiDimCount,
                MultiDimArrayBytes: multiDimBytes,
                LohArrayCount: lohCount,
                LohArrayBytes: lohBytes,
                TopArrayTypesBySize: topArrayTypes,
                TopLargeArrays: topLargeArrays,
                TopSparseArrays: topSparseArrays,
                PinnedArrayCount: pinnedArrayCount,
                PinnedArrayBytes: pinnedArrayBytes,
                TopPinnedArrays: topPinnedArrays);
        }

        // ── Sparse array walk helpers ───────────────────────────────────────────────

        private static int CountNullReferences(ClrArray arr)
        {
            int nullCount = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                ClrObject elem = arr.GetObjectValue(i);
                if (!elem.IsValid || elem.Address == 0) nullCount++;
            }
            return nullCount;
        }

        // Bounds peak allocation to SparseReadChunkSize * sizeof(T) regardless of array length,
        // so a single ReadValues<T> call never has to allocate a buffer as large as e.g. a
        // 100 MB LOH byte[].
        private const int SparseReadChunkSize = 65_536;

        private static int CountZeroElements(ClrArray arr, int length, ClrElementType elemType) => elemType switch
        {
            ClrElementType.Int8 => CountZeroElements<sbyte>(arr, length),
            ClrElementType.UInt8 => CountZeroElements<byte>(arr, length),
            ClrElementType.Int16 => CountZeroElements<short>(arr, length),
            ClrElementType.UInt16 => CountZeroElements<ushort>(arr, length),
            ClrElementType.Int32 => CountZeroElements<int>(arr, length),
            ClrElementType.UInt32 => CountZeroElements<uint>(arr, length),
            ClrElementType.Int64 => CountZeroElements<long>(arr, length),
            ClrElementType.UInt64 => CountZeroElements<ulong>(arr, length),
            ClrElementType.Float => CountZeroElements<float>(arr, length),
            ClrElementType.Double => CountZeroElements<double>(arr, length),
            _ => -1,
        };

        // ReadValues<T> returns null (not a shorter array, not zero-filled) when the requested
        // range is partially or fully unreadable — e.g. a truncated dump. That is surfaced here
        // as -1 so the caller skips the candidate rather than reporting a ratio computed over
        // fewer elements than the array's actual length.
        private static int CountZeroElements<T>(ClrArray arr, int length)
            where T : unmanaged, INumber<T>
        {
            int zeroCount = 0;
            for (int offset = 0; offset < length; offset += SparseReadChunkSize)
            {
                int take = Math.Min(SparseReadChunkSize, length - offset);
                T[]? chunk = arr.ReadValues<T>(offset, take);
                if (chunk is null) return -1;
                for (int i = 0; i < chunk.Length; i++)
                    if (chunk[i] == T.Zero) zeroCount++;
            }
            return zeroCount;
        }

        private static bool IsSupportedNumericElementType(ClrElementType elemType) => elemType switch
        {
            ClrElementType.Int8 or ClrElementType.UInt8
                or ClrElementType.Int16 or ClrElementType.UInt16
                or ClrElementType.Int32 or ClrElementType.UInt32
                or ClrElementType.Int64 or ClrElementType.UInt64
                or ClrElementType.Float or ClrElementType.Double => true,
            _ => false,
        };

        private static int ElementSizeBytes(ClrElementType elemType) => elemType switch
        {
            ClrElementType.Int8 or ClrElementType.UInt8 => sizeof(byte),
            ClrElementType.Int16 or ClrElementType.UInt16 => sizeof(short),
            ClrElementType.Int32 or ClrElementType.UInt32 or ClrElementType.Float => sizeof(int),
            ClrElementType.Int64 or ClrElementType.UInt64 or ClrElementType.Double => sizeof(long),
            _ => 0,
        };

        // ── Large array index reader ──────────────────────────────────────────────

        public void Dispose() { }
    }
}
