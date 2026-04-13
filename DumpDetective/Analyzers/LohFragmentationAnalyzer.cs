using Microsoft.Diagnostics.Runtime;
using DumpDetective.Models;
using DumpDetective.Utilities;
using System.Reflection;

namespace DumpDetective.Analyzers
{
    internal class LohFragmentationAnalyzer
    {
        private const int TopSegments = 10;
        private readonly OutputWriter _writer;

        public LohFragmentationAnalyzer(OutputWriter writer)
        {
            _writer = writer;
        }

        public AnalyzerOutput Analyze(ClrHeap heap)
        {
            _writer.WriteHeader("LOH FRAGMENTATION ANALYSIS:");

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

                double fragmentationPercent = totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;
                segmentStats.Add(new LohSegmentStats(GetSegmentAddress(segment), totalBytes, usedBytes, freeBytes, largestFreeBlock, objectCount, freeObjectCount, fragmentationPercent));
            }

            scanCounter.Complete();

            if (segmentStats.Count == 0)
            {
                _writer.WriteLine("No LOH segments found.");
                _writer.WriteLine(StringConstants.Equals80);
                return new AnalyzerOutput(
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
                    new LohFragmentationDomainResult(0, 0, 0, 0, 0));
            }

            double overallFragmentation = CalculateOverallFragmentationPercent(segmentStats);
            ulong totalAllBytes = 0, totalFreeBytes = 0, maxFreeBlock = 0;
            foreach (var s in segmentStats)
            {
                totalAllBytes += s.TotalBytes;
                totalFreeBytes += s.FreeBytes;
                if (s.LargestFreeBlock > maxFreeBlock) maxFreeBlock = s.LargestFreeBlock;
            }

            PrintSummary(segmentStats);
            PrintTopFragmentedSegments(segmentStats);
            _writer.WriteLine(StringConstants.Equals80);
            return new AnalyzerOutput(
                [CreateFinding(overallFragmentation, segmentStats.Count)],
                new LohFragmentationDomainResult(segmentStats.Count, totalAllBytes, totalFreeBytes, overallFragmentation, maxFreeBlock));
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

            var largeFlag = type.GetProperty("IsLargeObjectSegment", BindingFlags.Instance | BindingFlags.Public);
            if (largeFlag?.GetValue(segment) is bool isLargeObjectSegment)
                return isLargeObjectSegment;

            var kindProp = type.GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public);
            if (kindProp?.GetValue(segment) is not null)
            {
                string kindName = kindProp.GetValue(segment)!.ToString() ?? string.Empty;
                if (kindName.Contains("Large", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var isLarge = type.GetProperty("IsLarge", BindingFlags.Instance | BindingFlags.Public);
            if (isLarge?.GetValue(segment) is bool isLargeValue)
                return isLargeValue;

            return false;
        }

        private static ulong GetSegmentAddress(ClrSegment segment)
        {
            Type type = segment.GetType();

            var addressProp = type.GetProperty("Address", BindingFlags.Instance | BindingFlags.Public);
            if (addressProp?.GetValue(segment) is ulong address)
                return address;

            var objectRangeProp = type.GetProperty("ObjectRange", BindingFlags.Instance | BindingFlags.Public);
            if (objectRangeProp?.GetValue(segment) is not null)
            {
                object range = objectRangeProp.GetValue(segment)!;
                var startProp = range.GetType().GetProperty("Start", BindingFlags.Instance | BindingFlags.Public);
                if (startProp?.GetValue(range) is ulong start)
                    return start;
            }

            return 0;
        }

        private void PrintSummary(List<LohSegmentStats> segmentStats)
        {
            ulong totalBytes = 0;
            ulong usedBytes = 0;
            ulong freeBytes = 0;
            int totalObjects = 0;
            int totalFreeObjects = 0;

            foreach (var segment in segmentStats)
            {
                totalBytes += segment.TotalBytes;
                usedBytes += segment.UsedBytes;
                freeBytes += segment.FreeBytes;
                totalObjects += segment.ObjectCount;
                totalFreeObjects += segment.FreeObjectCount;
            }

            double fragmentationPercent = totalBytes == 0 ? 0 : freeBytes * 100.0 / totalBytes;

            _writer.WriteLine("LOH SUMMARY:");
            _writer.WriteSeparator();
            _writer.WriteLine($"LOH Segments: {segmentStats.Count:N0}");
            _writer.WriteLine($"LOH Total Size: {FormatHelper.FormatBytes(totalBytes)}");
            _writer.WriteLine($"LOH Used Size: {FormatHelper.FormatBytes(usedBytes)}");
            _writer.WriteLine($"LOH Free Size: {FormatHelper.FormatBytes(freeBytes)} ({fragmentationPercent:F1}% fragmentation)");
            _writer.WriteLine($"LOH Objects: {totalObjects:N0}");
            _writer.WriteLine($"LOH Free Blocks: {totalFreeObjects:N0}");
        }

        private void PrintTopFragmentedSegments(List<LohSegmentStats> segmentStats)
        {
            _writer.WriteLine("\nTOP FRAGMENTED LOH SEGMENTS:");
            _writer.WriteSeparator();
            _writer.WriteLine($"{"Segment",-18} {"Total",12} {"Free",12} {"Frag%",8} {"Largest Free",14}");
            _writer.WriteSeparator();

            int written = 0;
            foreach (var segment in segmentStats.OrderByDescending(s => s.FragmentationPercent))
            {
                if (written >= TopSegments)
                    break;

                _writer.WriteLine(
                    $"0x{segment.Address:X16} " +
                    $"{FormatHelper.FormatBytes(segment.TotalBytes),12} " +
                    $"{FormatHelper.FormatBytes(segment.FreeBytes),12} " +
                    $"{segment.FragmentationPercent,7:F1}% " +
                    $"{FormatHelper.FormatBytes(segment.LargestFreeBlock),14}");

                written++;
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
