using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;

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

        public string Name => "LOH Fragmentation Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LohFragmentationAnalysisOptions options = context.AnalysisOptions.LohFragmentationAnalysis;
            return ValueTask.FromResult(Analyze(context.Heap, context.Cache, context.Progress, options, cancellationToken).Stamp(this));
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
            {
                LohFragmentationAnalysisOptions options = new();
                return AnalyzeFromIndex(heap, heapIndex, progress, options, cancellationToken);
            }

            // Fallback: full segment object scan (benchmarks, tests, or no index available).
            return AnalyzeFromHeap(heap, progress);
        }

        private AnalyzerDomainResult Analyze(ClrHeap heap, IHeapAnalysisCache? cache, IProgress<AnalyzerProgressReport>? progress, LohFragmentationAnalysisOptions options, CancellationToken cancellationToken)
        {
            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
                return AnalyzeFromIndex(heap, heapIndex, progress, options, cancellationToken);

            return AnalyzeFromHeap(heap, progress, options);
        }

        private AnalyzerDomainResult AnalyzeFromHeap(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress)
            => AnalyzeFromHeap(heap, progress, new LohFragmentationAnalysisOptions());

        private AnalyzerDomainResult AnalyzeFromHeap(ClrHeap heap, IProgress<AnalyzerProgressReport>? progress, LohFragmentationAnalysisOptions options)
        {
            // NOTE: fallback path — used when no Phase 1 index is available.

            var segmentStats = new List<LohSegmentStats>();
            var allFreeSizes = new List<ulong>(capacity: 256);
            var largeObjectCandidates = new List<(ulong Address, string TypeName, ulong Size)>();
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
                int objectCount = 0;
                int freeObjectCount = 0;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    scanCounter.Tick();

                    if (!obj.IsValid)
                        continue;

                    ulong objectAddress = obj.Address;
                    if (objectAddress == 0)
                        continue;

                    AccumulateSegmentObjectByAddress(
                        heap,
                        objectAddress,
                        allFreeSizes,
                        largeObjectCandidates,
                        ref freeBytes,
                        ref largestFreeBlock,
                        ref objectCount,
                        ref freeObjectCount);
                }

                // Match disk mode's Step 3 derivation (totalBytes - freeBytes): keeps the
                // Total = Used + Free invariant consistent between modes now that
                // GetSegmentTotalBytes can include committed padding no per-object scan sees.
                ulong usedBytes = totalBytes > freeBytes ? totalBytes - freeBytes : 0;

                double fragmentationPercent = totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;
                segmentStats.Add(new LohSegmentStats(GetSegmentAddress(segment), totalBytes, usedBytes, freeBytes, largestFreeBlock, objectCount, freeObjectCount, fragmentationPercent));
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
            int topN = Math.Min(options.TopSegments, segmentStats.Count);
            var topSegments = new List<LohSegmentSnapshot>(topN);
            for (int i = 0; i < topN; i++)
                topSegments.Add(new LohSegmentSnapshot(segmentStats[i].Address, segmentStats[i].FragmentationPercent, segmentStats[i].FreeBytes, segmentStats[i].LargestFreeBlock));

            var freeGapHistogram = BuildFreeGapHistogram(allFreeSizes);

            largeObjectCandidates.Sort(static (a, b) => b.Size.CompareTo(a.Size));
            int topLargeN = Math.Min(options.TopLargeObjectsCount, largeObjectCandidates.Count);
            var topLargeObjects = new List<LargeObjectSnapshot>(topLargeN);
            for (int i = 0; i < topLargeN; i++)
                topLargeObjects.Add(new LargeObjectSnapshot(largeObjectCandidates[i].Address, largeObjectCandidates[i].TypeName, largeObjectCandidates[i].Size));

            return new LohFragmentationDomainResult(segmentStats.Count, totalAllBytes, totalFreeBytes, totalUsedBytes, totalFreeBlocks, overallFragmentation, maxFreeBlock, topSegments, freeGapHistogram, topLargeObjects);
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

        // Matches LohFreeBlockWriter.Write key which uses segment.Start.
        private static bool IsLohSegment(ClrSegment segment)
            => segment.Kind.ToString().Contains("Large", StringComparison.OrdinalIgnoreCase);

        private static ulong GetSegmentAddress(ClrSegment segment) => segment.Start;

        private static void AccumulateSegmentObjectByAddress(
            ClrHeap heap,
            ulong objectAddress,
            List<ulong> allFreeSizes,
            List<(ulong Address, string TypeName, ulong Size)> largeObjectCandidates,
            ref ulong freeBytes,
            ref ulong largestFreeBlock,
            ref int objectCount,
            ref int freeObjectCount)
        {
            if (objectAddress == 0)
                return;

            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid)
                return;

            if (obj.IsFree)
            {
                ulong size = obj.Size;
                freeObjectCount++;
                freeBytes += size;
                allFreeSizes.Add(size);
                if (size > largestFreeBlock)
                    largestFreeBlock = size;
            }
            else
            {
                objectCount++;

                ulong size = obj.Size;
                if (size >= LohThreshold)
                    largeObjectCandidates.Add((objectAddress, obj.Type?.Name ?? "Unknown", size));
            }
        }

        // ── Index-based fast path ─────────────────────────────────────────────────

        private AnalyzerDomainResult AnalyzeFromIndex(
            ClrHeap heap,
            HeapIndexBuildResult heapIndex,
            IProgress<AnalyzerProgressReport>? progress,
            LohFragmentationAnalysisOptions options,
            CancellationToken cancellationToken)
        {
            string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;

            // Memory mode: IndexPath is "<memory>", so indexDir is "". The satellite files
            // (LohFreeBlockIndex.bin, LargeObjectIndex.bin) only exist in disk mode.
            // Fall back to the full segment scan so both modes produce identical rich output.
            if (indexDir.Length == 0)
                return AnalyzeFromHeap(heap, progress, options);

            progress?.Report(new(0, "reading LOH segment metadata", null, TimeSpan.Zero));

            // Step 1: Read LOH segment committed bytes from heap metadata (no object enumeration).
            var segmentTotalBytes = new Dictionary<ulong, ulong>();
            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsLohSegment(segment))
                    continue;
                ulong addr = GetSegmentAddress(segment);
                ulong bytes = GetSegmentTotalBytes(segment);
                if (addr != 0)
                    segmentTotalBytes[addr] = bytes;
            }

            if (segmentTotalBytes.Count == 0)
                return new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0);

            // Step 2: Read LohFreeBlockIndex.bin.
            string lohFreeBlockPath = Path.Combine(indexDir, DumpIndexPaths.LohFreeBlockIndexFile);
            var freeBySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, int Count)>();
            var allFreeSizes = new List<ulong>(capacity: 256);
            if (File.Exists(lohFreeBlockPath))
            {
                progress?.Report(new(0, "reading LohFreeBlockIndex.bin", null, TimeSpan.Zero));
                ReadFreeBlocks(lohFreeBlockPath, freeBySegment, allFreeSizes, cancellationToken);
            }

            // Step 3: Compute per-segment and global stats.
            ulong totalAllBytes = 0, totalFreeBytes = 0, totalUsedBytes = 0, maxFreeBlock = 0;
            int totalFreeBlocks = 0;
            var segStats = new List<(ulong Address, double FragPct, ulong FreeBytes, ulong LargestFree)>(segmentTotalBytes.Count);

            foreach ((ulong addr, ulong totalBytes) in segmentTotalBytes)
            {
                ulong segFree = 0, segLargest = 0;
                int segFreeCount = 0;
                if (freeBySegment.TryGetValue(addr, out var fb))
                {
                    segFree = fb.TotalFree;
                    segLargest = fb.Largest;
                    segFreeCount = fb.Count;
                }
                ulong segUsed = totalBytes > segFree ? totalBytes - segFree : 0;
                double fragPct = totalBytes == 0 ? 0 : segFree * 100.0 / totalBytes;

                totalAllBytes += totalBytes;
                totalFreeBytes += segFree;
                totalUsedBytes += segUsed;
                totalFreeBlocks += segFreeCount;
                if (segLargest > maxFreeBlock) maxFreeBlock = segLargest;

                segStats.Add((addr, fragPct, segFree, segLargest));
            }

            double overallFragPct = totalAllBytes == 0 ? 0 : totalFreeBytes * 100.0 / totalAllBytes;

            // Sort top fragmented segments descending by fragmentation %, then free bytes.
            segStats.Sort(static (a, b) =>
            {
                int cmp = b.FragPct.CompareTo(a.FragPct);
                return cmp != 0 ? cmp : b.FreeBytes.CompareTo(a.FreeBytes);
            });

            var topSegs = new List<LohSegmentSnapshot>(Math.Min(options.TopSegments, segStats.Count));
            for (int i = 0; i < topSegs.Capacity; i++)
                topSegs.Add(new LohSegmentSnapshot(segStats[i].Address, segStats[i].FragPct, segStats[i].FreeBytes, segStats[i].LargestFree));

            // Step 4: Build free-gap histogram.
            var freeGapHistogram = BuildFreeGapHistogram(allFreeSizes);

            // Step 5: Read LargeObjectIndex.bin and resolve type names (≤ 100 objects).
            string largeObjPath = Path.Combine(indexDir, DumpIndexPaths.LargeObjectIndexFile);
            List<LargeObjectSnapshot> topLargeObjects = [];
            if (File.Exists(largeObjPath))
            {
                progress?.Report(new(0, "reading LargeObjectIndex.bin", null, TimeSpan.Zero));
                topLargeObjects = ReadTopLargeObjects(heap, largeObjPath, options.TopLargeObjectsCount, cancellationToken);
            }

            return new LohFragmentationDomainResult(
                segmentTotalBytes.Count, totalAllBytes, totalFreeBytes, totalUsedBytes,
                totalFreeBlocks, overallFragPct, maxFreeBlock,
                topSegs, freeGapHistogram, topLargeObjects);
        }

        // ── Segment metadata helpers ──────────────────────────────────────────────

        private static ulong GetSegmentTotalBytes(ClrSegment segment)
        {
            MemoryRange mem = segment.CommittedMemory;
            return mem.End >= mem.Start ? mem.End - mem.Start : 0;
        }

        // ── Index readers ─────────────────────────────────────────────────────────

        private static void ReadFreeBlocks(
            string filePath,
            Dictionary<ulong, (ulong TotalFree, ulong Largest, int Count)> bySegment,
            List<ulong> allSizes,
            CancellationToken cancellationToken)
        {
            const int RecordSize = 24; // SegmentAddress(8) | Offset(8) | Size(8)
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 128 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return;

            // Read record-by-record via ReadAtLeast: Stream.Read is not guaranteed to
            // return record-aligned byte counts, and a hand-rolled batch buffer with
            // manual carry-over has repeatedly gotten this wrong elsewhere. FileStream's
            // own internal buffer (see bufferSize above) makes this as cheap as batching.
            Span<byte> rec = stackalloc byte[RecordSize];
            for (long i = 0; i < header.RecordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false) < RecordSize)
                    break;

                ulong segAddr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                // offset field at [8..16) is unused for aggregation
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);

                allSizes.Add(size);
                if (bySegment.TryGetValue(segAddr, out var ex))
                    bySegment[segAddr] = (ex.TotalFree + size, size > ex.Largest ? size : ex.Largest, ex.Count + 1);
                else
                    bySegment[segAddr] = (size, size, 1);
            }
        }

        private static List<LargeObjectSnapshot> ReadTopLargeObjects(
            ClrHeap heap,
            string filePath,
                int topLargeObjectsCount,
            CancellationToken cancellationToken)
        {
            const int RecordSize = 24; // Address(8) | MT(8) | Size(8)
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return [];

            int cap = (int)Math.Min(header.RecordCount, topLargeObjectsCount);
            var result = new List<LargeObjectSnapshot>(cap);
            var typeByAddr = new Dictionary<ulong, string>(capacity: cap);

            Span<byte> rec = stackalloc byte[RecordSize];
            for (long i = 0; i < header.RecordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                if (read < RecordSize) break;

                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                // MT field (rec[8..]) unused — resolve via heap
                ulong size = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);

                ClrObject obj = heap.GetObject(address);
                if (!obj.IsValid) continue;

                string typeName = obj.Type?.Name ?? "Unknown";
                if (string.Equals(typeName, "Free", StringComparison.Ordinal)) continue;

                result.Add(new LargeObjectSnapshot(address, typeName, size));
                if (result.Count >= topLargeObjectsCount) break;
            }

            return result;
        }

        // ── Free-gap histogram ────────────────────────────────────────────────────

        private static List<FreeGapBucket> BuildFreeGapHistogram(List<ulong> allFreeSizes)
        {
            if (allFreeSizes.Count == 0) return [];

            int[] counts = new int[s_gapBuckets.Length];
            foreach (ulong size in allFreeSizes)
            {
                for (int b = 0; b < s_gapBuckets.Length; b++)
                {
                    if (size >= s_gapBuckets[b].Min && size < s_gapBuckets[b].Max)
                    {
                        counts[b]++;
                        break;
                    }
                }
            }

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
            public int ObjectCount { get; }
            public int FreeObjectCount { get; }
            public double FragmentationPercent { get; }

            public LohSegmentStats(
                ulong address,
                ulong totalBytes,
                ulong usedBytes,
                ulong freeBytes,
                ulong largestFreeBlock,
                int objectCount,
                int freeObjectCount,
                double fragmentationPercent)
            {
                Address = address;
                TotalBytes = totalBytes;
                UsedBytes = usedBytes;
                FreeBytes = freeBytes;
                LargestFreeBlock = largestFreeBlock;
                ObjectCount = objectCount;
                FreeObjectCount = freeObjectCount;
                FragmentationPercent = fragmentationPercent;
            }
        }

        public void Dispose() { }
    }
}


