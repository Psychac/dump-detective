using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Satellite;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 1 validation for docs/cache/cache-architecture.md: confirms
/// DiskBackedObjectIndexWriter actually produces a well-formed SegmentIndex section against a real
/// dump — record ranges are contiguous, sum to the total object count, and match the source
/// ClrSegment boundaries. Unlike SegmentIndexWriterTests (synthetic data), this exercises the real
/// writer wiring end-to-end.
/// </summary>
public sealed class SegmentIndexBuildDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public SegmentIndexBuildDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public void DiskBuild_ProducesWellFormedSegmentIndex_MatchingObjectAddressesAndSegments()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        string freshDumpPath = dumpPath + ".freshdiskcheck.SegmentIndexBuildDiscrepancyTests";
        string freshIndexDir = DumpIndexPaths.EnsureDirectory(freshDumpPath);
        // DiskBackedObjectIndexWriter.Build reads FileInfo(freshDumpPath).Length (bucket-count sizing
        // heuristic) and DumpContentHasher samples its bytes (container header hash) — both need a
        // real file at this synthetic path, even though its content is irrelevant to what this test
        // actually validates (SegmentIndex correctness, not content-hash/bucket-sizing correctness).
        File.WriteAllBytes(freshDumpPath, new byte[4096]);
        try
        {
            HeapAnalysisCache diskCache = new();
            diskCache.PrebuildHeapIndex(heap, freshDumpPath, CancellationToken.None, progress: null);

            string containerPath = DumpIndexPaths.CacheContainer(freshDumpPath);
            List<SegmentIndexEntry> segmentEntries = SegmentIndexWriter.ReadRecords(containerPath);

            CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader).Should().BeTrue();
            reader!.TryGetSectionInfo(CacheSectionId.ObjectAddresses, out CacheTocEntry objAddrEntry).Should().BeTrue();
            long totalObjectCount = objAddrEntry.RecordCount;

            _output.WriteLine($"segment index entries: {segmentEntries.Count}");
            _output.WriteLine($"total objects (ObjectAddresses TOC): {totalObjectCount}");

            segmentEntries.Should().NotBeEmpty();

            // Record ranges must be contiguous and non-overlapping in the order written (segment
            // index order), and must sum exactly to the total object count.
            long expectedNext = 0;
            long sumRecordCounts = 0;
            var violations = new List<string>();
            foreach (SegmentIndexEntry entry in segmentEntries)
            {
                if (entry.FirstRecordIndex != expectedNext)
                    violations.Add($"gap/overlap: expected FirstRecordIndex {expectedNext}, got {entry.FirstRecordIndex}");
                if (entry.RecordCount <= 0)
                    violations.Add($"non-positive RecordCount {entry.RecordCount} at FirstRecordIndex {entry.FirstRecordIndex}");
                if (entry.End <= entry.Start)
                    violations.Add($"degenerate segment range [0x{entry.Start:X}-0x{entry.End:X}]");

                expectedNext = entry.FirstRecordIndex + entry.RecordCount;
                sumRecordCounts += entry.RecordCount;
            }

            violations.Should().BeEmpty(string.Join("; ", violations));
            sumRecordCounts.Should().Be(totalObjectCount);

            // Cross-check against the live heap's segments: every segment with objects should be
            // represented (allowing for segments that legitimately hold zero objects, which are
            // omitted by design).
            var liveSegmentRanges = new HashSet<(ulong Start, ulong End)>();
            foreach (ClrSegment segment in heap.Segments)
                liveSegmentRanges.Add((segment.Start, segment.End));

            foreach (SegmentIndexEntry entry in segmentEntries)
                liveSegmentRanges.Should().Contain((entry.Start, entry.End));
        }
        finally
        {
            if (Directory.Exists(freshIndexDir))
                Directory.Delete(freshIndexDir, recursive: true);
            if (File.Exists(freshDumpPath))
                File.Delete(freshDumpPath);
        }
    }
}
