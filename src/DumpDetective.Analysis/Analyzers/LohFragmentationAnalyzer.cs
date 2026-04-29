using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Analyzers
{
    public class LohFragmentationAnalyzer : IAnalyzer
    {
        private const int TopSegments = 10;
        private const int TopLargeObjectsCount = 20;

        // OPT-#4: Cache resolved PropertyInfo/MethodInfo per ClrSegment concrete type to avoid
        // repeated reflection lookups (GetProperty calls) inside the per-segment hot loop.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, SegmentReflectionCache> s_segmentReflectionCache = new();

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

        private sealed class SegmentReflectionCache
        {
            public PropertyInfo? IsLargeObjectSegment { get; init; }
            public PropertyInfo? Kind { get; init; }
            public PropertyInfo? IsLarge { get; init; }
            public PropertyInfo? Address { get; init; }
            public PropertyInfo? Start { get; init; }
            public PropertyInfo? End { get; init; }
            public PropertyInfo? ObjectRange { get; init; }
            public PropertyInfo? CommittedMemory { get; init; }

            public static SegmentReflectionCache Build(Type type) => new()
            {
                IsLargeObjectSegment = type.GetProperty("IsLargeObjectSegment", BindingFlags.Instance | BindingFlags.Public),
                Kind = type.GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public),
                IsLarge = type.GetProperty("IsLarge", BindingFlags.Instance | BindingFlags.Public),
                Address = type.GetProperty("Address", BindingFlags.Instance | BindingFlags.Public),
                Start = type.GetProperty("Start", BindingFlags.Instance | BindingFlags.Public),
                End = type.GetProperty("End", BindingFlags.Instance | BindingFlags.Public),
                ObjectRange = type.GetProperty("ObjectRange", BindingFlags.Instance | BindingFlags.Public),
                CommittedMemory = type.GetProperty("CommittedMemory", BindingFlags.Instance | BindingFlags.Public),
            };
        }

        public string Name => "LOH Fragmentation Analysis";
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
            var scanCounter = new ObjectScanCounter("scanning LOH segments", progress, reportEveryObjects: 100_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsLohSegment(segment))
                    continue;

                ulong totalBytes = 0;
                ulong freeBytes = 0;
                ulong usedBytes = 0;
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
                        ref totalBytes,
                        ref freeBytes,
                        ref usedBytes,
                        ref largestFreeBlock,
                        ref objectCount,
                        ref freeObjectCount);
                }

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

            var topSegments = segmentStats
                .OrderByDescending(s => s.FragmentationPercent)
                .ThenByDescending(s => s.FreeBytes)
                .Take(TopSegments)
                .Select(s => new LohSegmentSnapshot(s.Address, s.FragmentationPercent, s.FreeBytes, s.LargestFreeBlock))
                .ToList();

            return new LohFragmentationDomainResult(segmentStats.Count, totalAllBytes, totalFreeBytes, totalUsedBytes, totalFreeBlocks, overallFragmentation, maxFreeBlock, topSegments);
        }

        private static InsightFinding CreateFinding(double fragmentationPercent, int segmentCount)
        {
            FindingSeverity severity = fragmentationPercent >= 30
                ? FindingSeverity.Critical
                : fragmentationPercent >= 15
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            return new InsightFinding(
                Analyzer: nameof(LohFragmentationAnalyzer),
                Category: "Fragmentation",
                Severity: severity,
                Title: "LOH fragmentation assessment",
                Evidence: $"{fragmentationPercent:F1}% overall free-space fragmentation across {segmentCount:N0} LOH segment(s).",
                Recommendation: severity == FindingSeverity.Critical
                    ? "Investigate large object allocation churn and retention; consider compaction strategies and pooling."
                    : severity == FindingSeverity.Warning
                        ? "Monitor LOH allocation patterns and reduce churn from short-lived large allocations."
                        : "LOH fragmentation is currently within acceptable range.",
                Tags: ["loh", "fragmentation", "memory"],
                MetricValue: fragmentationPercent,
                MetricUnit: "% fragmentation");
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

        private static bool IsLohSegment(ClrSegment segment)
        {
            Type type = segment.GetType();
            SegmentReflectionCache rc = s_segmentReflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

            if (rc.IsLargeObjectSegment?.GetValue(segment) is bool isLargeObjectSegment)
                return isLargeObjectSegment;

            if (rc.Kind?.GetValue(segment) is not null)
            {
                string kindName = rc.Kind.GetValue(segment)!.ToString() ?? string.Empty;
                if (kindName.Contains("Large", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (rc.IsLarge?.GetValue(segment) is bool isLargeValue)
                return isLargeValue;

            return false;
        }

        private static ulong GetSegmentAddress(ClrSegment segment)
        {
            Type type = segment.GetType();
            SegmentReflectionCache rc = s_segmentReflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

            // Prefer direct Start property (ClrMD 3.x standard; matches LohFreeBlockWriter.Write key).
            if (rc.Start?.GetValue(segment) is ulong start)
                return start;

            if (rc.Address?.GetValue(segment) is ulong address)
                return address;

            if (rc.ObjectRange?.GetValue(segment) is not null)
            {
                object range = rc.ObjectRange.GetValue(segment)!;
                var startProp = range.GetType().GetProperty("Start", BindingFlags.Instance | BindingFlags.Public);
                if (startProp?.GetValue(range) is ulong rangeStart)
                    return rangeStart;
            }

            return 0;
        }

        private static void AccumulateSegmentObjectByAddress(
            ClrHeap heap,
            ulong objectAddress,
            ref ulong totalBytes,
            ref ulong freeBytes,
            ref ulong usedBytes,
            ref ulong largestFreeBlock,
            ref int objectCount,
            ref int freeObjectCount)
        {
            if (objectAddress == 0)
                return;

            ClrObject obj = heap.GetObject(objectAddress);
            if (!obj.IsValid)
                return;

            ulong size = obj.Size;
            totalBytes += size;

            if (obj.IsFree)
            {
                freeObjectCount++;
                freeBytes += size;
                if (size > largestFreeBlock)
                    largestFreeBlock = size;
            }
            else
            {
                objectCount++;
                usedBytes += size;
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

            progress?.Report(new(0, "reading LOH segment metadata", null, TimeSpan.Zero));

            // Step 1: Read LOH segment committed bytes from heap metadata (no object enumeration).
            var segmentTotalBytes = new Dictionary<ulong, ulong>();
            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsLohSegment(segment))
                    continue;
                ulong addr  = GetSegmentAddress(segment);
                ulong bytes = GetSegmentTotalBytes(segment);
                if (addr != 0)
                    segmentTotalBytes[addr] = bytes;
            }

            if (segmentTotalBytes.Count == 0)
                return new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0);

            // Step 2: Read LohFreeBlockIndex.bin.
            string lohFreeBlockPath = Path.Combine(indexDir, DumpIndexPaths.LohFreeBlockIndexFile);
            var freeBySegment = new Dictionary<ulong, (ulong TotalFree, ulong Largest, int Count)>();
            var allFreeSizes  = new List<ulong>(capacity: 256);
            if (File.Exists(lohFreeBlockPath))
            {
                progress?.Report(new(0, "reading LohFreeBlockIndex.bin", null, TimeSpan.Zero));
                ReadFreeBlocks(lohFreeBlockPath, freeBySegment, allFreeSizes, cancellationToken);
            }

            // Step 3: Compute per-segment and global stats.
            ulong totalAllBytes = 0, totalFreeBytes = 0, totalUsedBytes = 0, maxFreeBlock = 0;
            int   totalFreeBlocks = 0;
            var   segStats = new List<(ulong Address, double FragPct, ulong FreeBytes, ulong LargestFree)>(segmentTotalBytes.Count);

            foreach ((ulong addr, ulong totalBytes) in segmentTotalBytes)
            {
                ulong segFree = 0, segLargest = 0;
                int   segFreeCount = 0;
                if (freeBySegment.TryGetValue(addr, out var fb))
                {
                    segFree     = fb.TotalFree;
                    segLargest  = fb.Largest;
                    segFreeCount = fb.Count;
                }
                ulong  segUsed  = totalBytes > segFree ? totalBytes - segFree : 0;
                double fragPct  = totalBytes == 0 ? 0 : segFree * 100.0 / totalBytes;

                totalAllBytes  += totalBytes;
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

            var topSegs = new List<LohSegmentSnapshot>(Math.Min(TopSegments, segStats.Count));
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
                topLargeObjects = ReadTopLargeObjects(heap, largeObjPath, cancellationToken);
            }

            return new LohFragmentationDomainResult(
                segmentTotalBytes.Count, totalAllBytes, totalFreeBytes, totalUsedBytes,
                totalFreeBlocks, overallFragPct, maxFreeBlock,
                topSegs, freeGapHistogram, topLargeObjects);
        }

        // ── Segment metadata helpers ──────────────────────────────────────────────

        private static ulong GetSegmentTotalBytes(ClrSegment segment)
        {
            Type type = segment.GetType();
            SegmentReflectionCache rc = s_segmentReflectionCache.GetOrAdd(type, SegmentReflectionCache.Build);

            // 1. CommittedMemory.Length (ClrMD 3.x MemoryRange struct).
            if (rc.CommittedMemory?.GetValue(segment) is { } mem)
            {
                var lenProp = mem.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public);
                if (lenProp?.GetValue(mem) is ulong len && len > 0)
                    return len;
            }

            // 2. Direct Start / End properties (ClrMD 3.x standard path).
            if (rc.Start?.GetValue(segment) is ulong start && rc.End?.GetValue(segment) is ulong end && end > start)
                return end - start;

            // 3. ObjectRange.Start / ObjectRange.End fallback.
            if (rc.ObjectRange?.GetValue(segment) is { } range)
            {
                var rt = range.GetType();
                var sp = rt.GetProperty("Start", BindingFlags.Instance | BindingFlags.Public);
                var ep = rt.GetProperty("End",   BindingFlags.Instance | BindingFlags.Public);
                if (sp?.GetValue(range) is ulong rs && ep?.GetValue(range) is ulong re && re > rs)
                    return re - rs;
            }

            return 0;
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

            if (!IndexHeader.TryRead(stream, out _))
                return;

            byte[] buf = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);
            try
            {
                int bytesRead;
                while ((bytesRead = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int records = bytesRead / RecordSize;
                    for (int i = 0; i < records; i++)
                    {
                        int   off     = i * RecordSize;
                        ulong segAddr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off));
                        // offset field at off+8 is unused for aggregation
                        ulong size    = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 16));

                        allSizes.Add(size);
                        if (bySegment.TryGetValue(segAddr, out var ex))
                            bySegment[segAddr] = (ex.TotalFree + size, size > ex.Largest ? size : ex.Largest, ex.Count + 1);
                        else
                            bySegment[segAddr] = (size, size, 1);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        private static List<LargeObjectSnapshot> ReadTopLargeObjects(
            ClrHeap heap,
            string filePath,
            CancellationToken cancellationToken)
        {
            const int RecordSize = 24; // Address(8) | MT(8) | Size(8)
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return [];

            int cap = (int)Math.Min(header.RecordCount, TopLargeObjectsCount);
            var result     = new List<LargeObjectSnapshot>(cap);
            var typeByAddr = new Dictionary<ulong, string>(capacity: cap);

            Span<byte> rec = stackalloc byte[RecordSize];
            for (long i = 0; i < header.RecordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                if (read < RecordSize) break;

                ulong address = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                // MT field (rec[8..]) unused — resolve via heap
                ulong size    = BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]);

                ClrObject obj = heap.GetObject(address);
                if (!obj.IsValid) continue;

                string typeName = obj.Type?.Name ?? "Unknown";
                if (string.Equals(typeName, "Free", StringComparison.Ordinal)) continue;

                result.Add(new LargeObjectSnapshot(address, typeName, size));
                if (result.Count >= TopLargeObjectsCount) break;
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
    }
}


