using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using System.Reflection;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Analysis.Analyzers
{
    public class LohFragmentationAnalyzer : IAnalyzer
    {
        private const int TopSegments = 10;

        // OPT-#4: Cache resolved PropertyInfo/MethodInfo per ClrSegment concrete type to avoid
        // repeated reflection lookups (GetProperty calls) inside the per-segment hot loop.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, SegmentReflectionCache> s_segmentReflectionCache = new();

        private sealed class SegmentReflectionCache
        {
            public PropertyInfo? IsLargeObjectSegment { get; init; }
            public PropertyInfo? Kind { get; init; }
            public PropertyInfo? IsLarge { get; init; }
            public PropertyInfo? Address { get; init; }
            public PropertyInfo? ObjectRange { get; init; }

            public static SegmentReflectionCache Build(Type type) => new()
            {
                IsLargeObjectSegment = type.GetProperty("IsLargeObjectSegment", BindingFlags.Instance | BindingFlags.Public),
                Kind = type.GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public),
                IsLarge = type.GetProperty("IsLarge", BindingFlags.Instance | BindingFlags.Public),
                Address = type.GetProperty("Address", BindingFlags.Instance | BindingFlags.Public),
                ObjectRange = type.GetProperty("ObjectRange", BindingFlags.Instance | BindingFlags.Public),
            };
        }

        public string Name => "LOH Fragmentation Analysis";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnalyzerExecutionResult executionResult = Analyze(context.Heap);
            return ValueTask.FromResult(AnalyzerDomainResultFactory.FromExecutionResult(this, executionResult));
        }

        public AnalyzerExecutionResult Analyze(ClrHeap heap)
        {
            // NOTE: Intentionally does not use IHeapAnalysisCache / heap index here.
            // Fragmentation analysis requires per-segment object ordering and contiguous
            // free-block detection — a flat address/MT/size index cannot express this.
            // The segment-level scan is the correct and only viable approach.

            var segmentStats = new List<LohSegmentStats>();
            var scanCounter = new ObjectScanCounter("LOH object scan", reportEveryObjects: 100_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

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
                return new AnalyzerExecutionResult(
                    [new InsightFinding(
                        Analyzer: nameof(LohFragmentationAnalyzer),
                        Category: "Performance",
                        Severity: FindingSeverity.Info,
                        Title: "No LOH segments were detected",
                        Evidence: "Heap scan did not report large-object-heap segments.",
                        Recommendation: "No LOH-fragmentation action required for this dump.",
                        Tags: ["loh", "fragmentation"],
                        MetricValue: 0,
                        MetricUnit: "% fragmentation")],
                    new LohFragmentationDomainResult(0, 0, 0, 0, 0, 0, 0));
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

            return new AnalyzerExecutionResult(
                [CreateFinding(overallFragmentation, segmentStats.Count)],
                new LohFragmentationDomainResult(segmentStats.Count, totalAllBytes, totalFreeBytes, totalUsedBytes, totalFreeBlocks, overallFragmentation, maxFreeBlock, topSegments));
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

            if (rc.Address?.GetValue(segment) is ulong address)
                return address;

            if (rc.ObjectRange?.GetValue(segment) is not null)
            {
                object range = rc.ObjectRange.GetValue(segment)!;
                var startProp = range.GetType().GetProperty("Start", BindingFlags.Instance | BindingFlags.Public);
                if (startProp?.GetValue(range) is ulong start)
                    return start;
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


