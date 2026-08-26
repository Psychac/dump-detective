using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Analysis.Indexing.Satellite;

using Microsoft.Diagnostics.Runtime;

using System.Buffers.Binary;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class LohFragmentationAnalyzer : IAnalyzer
    {
        // Matches LargeObjectTracker's LOH threshold so both modes select the same candidates.
        private const ulong LohThreshold = 85_000;

        // Free-gap histogram bucket boundaries (minSize inclusive, maxSize exclusive).
        private static readonly (ulong Min, ulong Max, string Label)[] s_gapBuckets =
        [
            (0,              1_024UL,            "< 1 KB"),
            (1_024UL,        65_536UL,           "1 KB \u2013 64 KB"),
            (65_536UL,       524_288UL,          "64 KB \u2013 512 KB"),
            (524_288UL,      1_048_576UL,        "512 KB \u2013 1 MB"),
            (1_048_576UL,    10_485_760UL,       "1 MB \u2013 10 MB"),
            (10_485_760UL,   104_857_600UL,      "10 MB \u2013 100 MB"),
            (104_857_600UL,  ulong.MaxValue,     "\u2265 100 MB"),
        ];

        public string Name => "LOH & POH Fragmentation Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, cancellationToken).Stamp(this));
        }

        /// <summary>Entry point for benchmarks and direct callers (no cache — falls back to heap scan).</summary>
        public AnalyzerDomainResult Analyze(ClrHeap heap)
        {
            return AnalyzeFromHeap(heap, progress: null);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            // Fast path: use Phase 1 pre-built LOH indices — no per-segment EnumerateObjects call.
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                return AnalyzeFromIndex(heap, heapIndex, progress, cancellationToken);

            // Fallback: full segment object scan (benchmarks, tests, or no index available).
            return AnalyzeFromHeap(heap, progress);
        }

        private AnalyzerDomainResult AnalyzeFromHeap(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
        {
            // NOTE: fallback path — used when no Phase 1 index is available.

            var segmentStats = new List<LohSegmentStats>();
            int[] freeGapBucketCounts = new int[s_gapBuckets.Length];
            var largeObjectCandidates = new List<(ulong Address, string TypeName, ulong Size)>();
            var typeAggregation = new Dictionary<string, (int Count, ulong TotalBytes)>();
            var scanCounter = new ObjectScanCounter("scanning LOH segments", progress, reportEveryObjects: 100_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsLohSegment(segment))
                    continue;

                // Match disk mode's Step 1 (GetSegmentTotalBytes): committed segment span,
                // not the sum of enumerable object sizes — those can differ by the
                // reserve/alignment padding past the last object.
                ulong totalBytes = GetSegmentTotalBytes(segment);
                ulong freeBytes = 0;
                ulong largestFreeBlock = 0;
                ulong largestFreeBlockAddress = 0;
                int objectCount = 0;
                int freeObjectCount = 0;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    scanCounter.Tick();

                    if (!obj.IsValid || obj.Address == 0)
                        continue;

                    AccumulateSegmentObject(
                        obj,
                        freeGapBucketCounts,
                        largeObjectCandidates,
                        typeAggregation,
                        ref freeBytes,
                        ref largestFreeBlock,
                        ref largestFreeBlockAddress,
                        ref objectCount,
                        ref freeObjectCount);
                }

                // Match disk mode's Step 3 derivation (totalBytes - freeBytes): keeps the
                // Total = Used + Free invariant consistent between modes now that
                // GetSegmentTotalBytes can include committed padding no per-object scan sees.
                ulong usedBytes = totalBytes > freeBytes ? totalBytes - freeBytes : 0;

                double fragmentationPercent = totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;
                segmentStats.Add(new LohSegmentStats(GetSegmentAddress(segment), totalBytes, usedBytes, freeBytes, largestFreeBlock, largestFreeBlockAddress, objectCount, freeObjectCount, fragmentationPercent, SegmentKindMapper.Map(segment)));
            }

            scanCounter.Complete();

            if (segmentStats.Count == 0)
            {
                return new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0);
            }

            double overallFragmentation = CalculateOverallFragmentationPercent(segmentStats);
            ulong totalAllBytes = 0, totalUsedBytes = 0, totalFreeBytes = 0, maxFreeBlock = 0;
            int totalFreeBlocks = 0;
            foreach (var s in segmentStats)
            {
                totalAllBytes += s.TotalBytes;
                totalUsedBytes += s.UsedBytes;
                totalFreeBytes += s.FreeBytes;
                totalFreeBlocks += s.FreeObjectCount;
                if (s.LargestFreeBlock > maxFreeBlock) maxFreeBlock = s.LargestFreeBlock;
            }

            segmentStats.Sort(static (a, b) =>
            {
                int cmp = b.FragmentationPercent.CompareTo(a.FragmentationPercent);
                return cmp != 0 ? cmp : b.FreeBytes.CompareTo(a.FreeBytes);
            });
            var topSegments = new List<LohSegmentSnapshot>(segmentStats.Count);
            var kindInputs = new List<(HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)>(segmentStats.Count);
            foreach (var s in segmentStats)
            {
                topSegments.Add(new LohSegmentSnapshot(s.Address, s.TotalBytes, s.FragmentationPercent, s.FreeBytes, s.LargestFreeBlock, s.LargestFreeBlockAddress, s.Kind));
                kindInputs.Add((s.Kind, s.TotalBytes, s.FreeBytes, s.UsedBytes, s.LargestFreeBlock));
            }

            List<LohKindBreakdown> kindBreakdown = BuildKindBreakdown(kindInputs);

            var freeGapHistogram = new List<FreeGapBucket>(s_gapBuckets.Length);
            for (int b = 0; b < s_gapBuckets.Length; b++)
                if (freeGapBucketCounts[b] > 0)
                    freeGapHistogram.Add(new FreeGapBucket(s_gapBuckets[b].Label, freeGapBucketCounts[b]));

            largeObjectCandidates.Sort(static (a, b) => b.Size.CompareTo(a.Size));
            var topLargeObjects = new List<LargeObjectSnapshot>(largeObjectCandidates.Count);
            foreach (var cand in largeObjectCandidates)
                topLargeObjects.Add(new LargeObjectSnapshot(cand.Address, cand.TypeName, cand.Size));

            // Build type-aggregated LOH consumption view: top types by total bytes.
            var typeProfiles = new List<LohTypeProfile>(typeAggregation.Count);
            foreach ((string typeName, (int count, ulong totalBytes)) in typeAggregation)
                typeProfiles.Add(new LohTypeProfile(typeName, count, totalBytes));
            typeProfiles.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

            return new LohFragmentationDomainResult(segmentStats.Count, totalAllBytes, totalFreeBytes, totalUsedBytes, totalFreeBlocks, overallFragmentation, maxFreeBlock, topSegments, freeGapHistogram, topLargeObjects, typeProfiles, kindBreakdown);
        }

        private static double CalculateOverallFragmentationPercent(List<LohSegmentStats> segmentStats)
        {
            ulong totalBytes = 0;
            ulong freeBytes = 0;

            foreach (var segment in segmentStats)
            {
                totalBytes += segment.TotalBytes;
                freeBytes += segment.FreeBytes;
            }

            return totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;
        }

        // ── LOH/POH kind breakdown ───────────────────────────────────────────────

        /// <summary>
        /// Groups per-segment stats by <see cref="HeapSegmentKind"/> (Large vs. Pinned) so the
        /// report can distinguish LOH from POH fragmentation instead of only showing the combined
        /// total that both heap-scan and index paths compute.
        /// </summary>
        internal static List<LohKindBreakdown> BuildKindBreakdown(
            IEnumerable<(HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)> segments)
        {
            var byKind = new Dictionary<HeapSegmentKind, (int Count, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)>();
            foreach (var s in segments)
            {
                if (byKind.TryGetValue(s.Kind, out var acc))
                    byKind[s.Kind] = (
                        acc.Count + 1,
                        acc.TotalBytes + s.TotalBytes,
                        acc.FreeBytes + s.FreeBytes,
                        acc.UsedBytes + s.UsedBytes,
                        s.LargestFreeBlock > acc.LargestFreeBlock ? s.LargestFreeBlock : acc.LargestFreeBlock);
                else
                    byKind[s.Kind] = (1, s.TotalBytes, s.FreeBytes, s.UsedBytes, s.LargestFreeBlock);
            }

            var result = new List<LohKindBreakdown>(byKind.Count);
            foreach (var (kind, acc) in byKind)
            {
                double fragPct = acc.TotalBytes == 0 ? 0 : acc.FreeBytes * 100.0 / acc.TotalBytes;
                result.Add(new LohKindBreakdown(kind, acc.Count, acc.TotalBytes, acc.FreeBytes, acc.UsedBytes, fragPct, acc.LargestFreeBlock));
            }
            result.Sort(static (a, b) => a.Kind.CompareTo(b.Kind));
            return result;
        }

        // Matches LohFreeBlockWriter.Write which indexes both Large and Pinned segments.
        private static bool IsLohSegment(ClrSegment segment) => IsLohSegment(segment.Kind);

        internal static bool IsLohSegment(GCSegmentKind kind)
            => kind == GCSegmentKind.Large || kind == GCSegmentKind.Pinned;

        private static ulong GetSegmentAddress(ClrSegment segment) => segment.Start;

        private static void AccumulateSegmentObject(
            ClrObject obj,
            int[] freeGapBucketCounts,
            List<(ulong Address, string TypeName, ulong Size)> largeObjectCandidates,
            Dictionary<string, (int Count, ulong TotalBytes)> typeAggregation,
            ref ulong freeBytes,
            ref ulong largestFreeBlock,
            ref ulong largestFreeBlockAddress,
            ref int objectCount,
            ref int freeObjectCount)
        {
            if (obj.IsFree)
            {
                ulong size = obj.Size;
                freeObjectCount++;
                freeBytes += size;

                // Accumulate directly into bucket counts instead of intermediate list (reduces memory on highly fragmented heaps).
                for (int b = 0; b < s_gapBuckets.Length; b++)
                {
                    if (size >= s_gapBuckets[b].Min && size < s_gapBuckets[b].Max)
                    {
                        freeGapBucketCounts[b]++;
                        break;
                    }
                }

                if (size > largestFreeBlock)
                {
                    largestFreeBlock = size;
                    largestFreeBlockAddress = obj.Address;
                }
            }
            else
            {
                objectCount++;

                ulong size = obj.Size;
                string typeName = obj.Type?.Name ?? "Unknown";

                // Aggregate by type for type-grouped LOH consumption view.
                if (typeAggregation.TryGetValue(typeName, out var existing))
                    typeAggregation[typeName] = (existing.Count + 1, existing.TotalBytes + size);
                else
                    typeAggregation[typeName] = (1, size);

                if (size >= LohThreshold)
                {
                    // Unbounded: LOH-threshold-sized objects (>= 85 KB) are a small fraction of any
                    // real heap's population, so keeping every candidate and sorting once at the end
                    // costs single-digit MB even on a 25 GB dump.
                    largeObjectCandidates.Add((obj.Address, typeName, size));
                }
            }
        }

        // ── Index-based fast path ─────────────────────────────────────────────────

        private AnalyzerDomainResult AnalyzeFromIndex(
            ClrHeap heap,
            HeapIndexBuildResult heapIndex,
            IProgress<AnalyzerProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;

            // Memory mode: IndexPath is "<memory>", so indexDir is "". The satellite files
            // (LohFreeBlockIndex.bin, LargeObjectIndex.bin) only exist in disk mode.
            // Fall back to the full segment scan so both modes produce identical rich output.
            if (indexDir.Length == 0)
                return AnalyzeFromHeap(heap, progress);

            progress?.Report(new(0, "reading LOH segment metadata", null, TimeSpan.Zero));

            // Step 1: Read LOH segment committed bytes from heap metadata (no object enumeration).
            var segmentTotalBytes = new Dictionary<ulong, ulong>();
            var segmentKinds = new Dictionary<ulong, HeapSegmentKind>();
            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsLohSegment(segment))
                    continue;
                ulong addr = GetSegmentAddress(segment);
                ulong bytes = GetSegmentTotalBytes(segment);
                if (addr != 0)
                {
                    segmentTotalBytes[addr] = bytes;
                    segmentKinds[addr] = SegmentKindMapper.Map(segment);
                }
            }

            if (segmentTotalBytes.Count == 0)
                return new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0);

            // Step 2: Read LohFreeBlockIndex.bin.
            var freeBySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)>();
            var allFreeSizes = new List<ulong>(capacity: 256);
            progress?.Report(new(0, "reading LohFreeBlockIndex.bin", null, TimeSpan.Zero));
            ReadFreeBlocks(heapIndex.IndexPath, freeBySegment, allFreeSizes, cancellationToken);

            // Step 3: Compute per-segment and global stats.
            ulong totalAllBytes = 0, totalFreeBytes = 0, totalUsedBytes = 0, maxFreeBlock = 0;
            int totalFreeBlocks = 0;
            var segStats = new List<(ulong Address, ulong TotalBytes, double FragPct, ulong FreeBytes, ulong LargestFree, ulong LargestFreeAddress, HeapSegmentKind Kind)>(segmentTotalBytes.Count);

            foreach ((ulong addr, ulong totalBytes) in segmentTotalBytes)
            {
                ulong segFree = 0, segLargest = 0, segLargestAddress = 0;
                int segFreeCount = 0;
                if (freeBySegment.TryGetValue(addr, out var fb))
                {
                    segFree = fb.TotalFree;
                    segLargest = fb.Largest;
                    segLargestAddress = fb.LargestAddress;
                    segFreeCount = fb.Count;
                }
                ulong segUsed = totalBytes > segFree ? totalBytes - segFree : 0;
                double fragPct = totalBytes == 0 ? 0 : segFree * 100.0 / totalBytes;

                totalAllBytes += totalBytes;
                totalFreeBytes += segFree;
                totalUsedBytes += segUsed;
                totalFreeBlocks += segFreeCount;
                if (segLargest > maxFreeBlock) maxFreeBlock = segLargest;

                segStats.Add((addr, totalBytes, fragPct, segFree, segLargest, segLargestAddress, segmentKinds[addr]));
            }

            double overallFragPct = totalAllBytes == 0 ? 0 : totalFreeBytes * 100.0 / totalAllBytes;

            // Sort top fragmented segments descending by fragmentation %, then free bytes.
            segStats.Sort(static (a, b) =>
            {
                int cmp = b.FragPct.CompareTo(a.FragPct);
                return cmp != 0 ? cmp : b.FreeBytes.CompareTo(a.FreeBytes);
            });

            var topSegs = new List<LohSegmentSnapshot>(segStats.Count);
            var kindInputs = new List<(HeapSegmentKind Kind, ulong TotalBytes, ulong FreeBytes, ulong UsedBytes, ulong LargestFreeBlock)>(segStats.Count);
            foreach (var s in segStats)
            {
                topSegs.Add(new LohSegmentSnapshot(s.Address, s.TotalBytes, s.FragPct, s.FreeBytes, s.LargestFree, s.LargestFreeAddress, s.Kind));
                ulong segUsedForKind = s.TotalBytes > s.FreeBytes ? s.TotalBytes - s.FreeBytes : 0;
                kindInputs.Add((s.Kind, s.TotalBytes, s.FreeBytes, segUsedForKind, s.LargestFree));
            }
            List<LohKindBreakdown> kindBreakdown = BuildKindBreakdown(kindInputs);

            // Step 4: Build free-gap histogram.
            var freeGapHistogram = BuildFreeGapHistogram(allFreeSizes, cancellationToken);

            // Step 5: Read LargeObjectIndex.bin and resolve every record's type name — the file
            // already only contains LOH-threshold-sized objects, so this is bounded by the same
            // small population AccumulateSegmentObject relies on in the heap-scan path.
            List<LargeObjectSnapshot> topLargeObjects = [];
            var typeAggregation = new Dictionary<string, (int Count, ulong TotalBytes)>();
            progress?.Report(new(0, "reading LargeObjectIndex.bin", null, TimeSpan.Zero));
            LargeObjectTracker.ReadRecords(heapIndex.IndexPath, (address, mt, size) => {
                // OPT (docs/cache/cache-architecture.md Phase 5): mt is already a
                // parameter of this callback — resolve via the metadata cache instead of
                // materializing a ClrObject. A null type is the equivalent "unresolvable" gate
                // heap.GetObject(address).IsValid served before.
                ClrType? type = heap.GetTypeByMethodTable(mt);
                if (type is null) return;
                string typeName = type.Name ?? "Unknown";
                if (string.Equals(typeName, "Free", StringComparison.Ordinal)) return;
                topLargeObjects.Add(new LargeObjectSnapshot(address, typeName, size));

                // Aggregate by type for type-grouped LOH consumption view.
                if (typeAggregation.TryGetValue(typeName, out var existing))
                    typeAggregation[typeName] = (existing.Count + 1, existing.TotalBytes + size);
                else
                    typeAggregation[typeName] = (1, size);
            }, cancellationToken);

            // Rank by size descending — the index file itself carries no size ordering.
            topLargeObjects.Sort(static (a, b) => b.Size.CompareTo(a.Size));

            // Build type-aggregated LOH consumption view: top types by total bytes.
            var typeProfiles = new List<LohTypeProfile>(typeAggregation.Count);
            foreach ((string typeName, (int count, ulong totalBytes)) in typeAggregation)
                typeProfiles.Add(new LohTypeProfile(typeName, count, totalBytes));
            typeProfiles.Sort(static (a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

            return new LohFragmentationDomainResult(
                segmentTotalBytes.Count, totalAllBytes, totalFreeBytes, totalUsedBytes,
                totalFreeBlocks, overallFragPct, maxFreeBlock,
                topSegs, freeGapHistogram, topLargeObjects, typeProfiles, kindBreakdown);
        }

        // ── Segment metadata helpers ──────────────────────────────────────────────

        private static ulong GetSegmentTotalBytes(ClrSegment segment)
        {
            MemoryRange mem = segment.CommittedMemory;
            return mem.End >= mem.Start ? mem.End - mem.Start : 0;
        }

        // ── Index readers ─────────────────────────────────────────────────────────

        internal static void ReadFreeBlocks(
            string containerPath,
            Dictionary<ulong, (ulong TotalFree, ulong Largest, ulong LargestAddress, int Count)> bySegment,
            List<ulong> allSizes,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!CacheSectionHelper.TryOpenCacheSection(containerPath, CacheSectionId.LohFreeBlocks, out Stream? stream) || stream is null)
                    return;

                using (stream)
                {
                    if (!IndexHeader.TryRead(stream, out IndexHeader header))
                        return;

                    const int RecordSize = 24; // SegmentAddress(8) | Offset(8) | Size(8)
                    Span<byte> rec = stackalloc byte[RecordSize];
                    for (long i = 0; i < header.RecordCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false) < RecordSize)
                            break;

                        ulong segAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                        ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
                        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);
                        ulong address = segAddr + offset;

                        allSizes.Add(size);
                        if (bySegment.TryGetValue(segAddr, out var ex))
                            bySegment[segAddr] = size > ex.Largest
                                ? (ex.TotalFree + size, size, address, ex.Count + 1)
                                : (ex.TotalFree + size, ex.Largest, ex.LargestAddress, ex.Count + 1);
                        else
                            bySegment[segAddr] = (size, size, address, 1);
                    }
                }
            }
            catch (Exception)
            {
                // Section not found or read failed; caller will process without free blocks.
            }
        }


        // ── Free-gap histogram ────────────────────────────────────────────────────

        internal static List<FreeGapBucket> BuildFreeGapHistogram(List<ulong> allFreeSizes, CancellationToken cancellationToken = default)
        {
            if (allFreeSizes.Count == 0) return [];

            int[] counts = new int[s_gapBuckets.Length];
            foreach (ulong size in allFreeSizes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int b = 0; b < s_gapBuckets.Length; b++)
                {
                    if (size >= s_gapBuckets[b].Min && size < s_gapBuckets[b].Max)
                    {
                        counts[b]++;
                        break;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<FreeGapBucket>(s_gapBuckets.Length);
            for (int b = 0; b < s_gapBuckets.Length; b++)
                if (counts[b] > 0)
                    result.Add(new FreeGapBucket(s_gapBuckets[b].Label, counts[b]));
            return result;
        }

        // ── Heap-scan fallback ────────────────────────────────────────────────────

        private sealed class LohSegmentStats
        {
            public ulong Address { get; }
            public ulong TotalBytes { get; }
            public ulong UsedBytes { get; }
            public ulong FreeBytes { get; }
            public ulong LargestFreeBlock { get; }
            public ulong LargestFreeBlockAddress { get; }
            public int ObjectCount { get; }
            public int FreeObjectCount { get; }
            public double FragmentationPercent { get; }
            public HeapSegmentKind Kind { get; }

            public LohSegmentStats(
                ulong address,
                ulong totalBytes,
                ulong usedBytes,
                ulong freeBytes,
                ulong largestFreeBlock,
                ulong largestFreeBlockAddress,
                int objectCount,
                int freeObjectCount,
                double fragmentationPercent,
                HeapSegmentKind kind)
            {
                Address = address;
                TotalBytes = totalBytes;
                UsedBytes = usedBytes;
                FreeBytes = freeBytes;
                LargestFreeBlock = largestFreeBlock;
                LargestFreeBlockAddress = largestFreeBlockAddress;
                ObjectCount = objectCount;
                FreeObjectCount = freeObjectCount;
                FragmentationPercent = fragmentationPercent;
                Kind = kind;
            }
        }

        public void Dispose() { }
    }
}


