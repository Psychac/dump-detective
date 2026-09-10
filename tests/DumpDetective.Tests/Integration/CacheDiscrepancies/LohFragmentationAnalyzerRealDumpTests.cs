using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>LohFragmentationAnalyzerRetypingCharacterizationTests</c>) only
/// exercises the no-index fallback path; this exercises the real disk-index fast path — real
/// <c>LohFreeBlockIndex.bin</c>/<c>LargeObjectIndex.bin</c> reads, real captured-large-object
/// sample, real per-type LOH/POH consumption from <c>TypeAggregates</c> — against a large real dump.
/// </summary>
public sealed class LohFragmentationAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public LohFragmentationAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentIndexBackedResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        int expectedSegmentCount = 0;
        ulong expectedTotalBytes = 0;
        foreach (ClrSegment segment in heap.Segments)
        {
            if (segment.Kind is GCSegmentKind.Large or GCSegmentKind.Pinned)
            {
                expectedSegmentCount++;
                expectedTotalBytes += SegmentKindMapper.GetCommittedBytes(segment);
            }
        }

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using LohFragmentationAnalyzerLegacyAdapter adapter = new();
        var result = (LohFragmentationDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("LOH & POH Fragmentation Analysis");
        result.SegmentCount.Should().Be(expectedSegmentCount);
        result.TotalBytes.Should().Be(expectedTotalBytes);
        result.FragmentationPercent.Should().BeInRange(0.0, 100.0);

        ulong sumKindBytes = 0;
        int sumKindSegments = 0;
        foreach (LohKindBreakdown kind in result.KindBreakdown!)
        {
            sumKindBytes += kind.TotalBytes;
            sumKindSegments += kind.SegmentCount;
            kind.FragmentationPercent.Should().BeInRange(0.0, 100.0);
        }
        sumKindBytes.Should().Be(result.TotalBytes);
        sumKindSegments.Should().Be(result.SegmentCount);

        IReadOnlyList<LohSegmentSnapshot> topFragmented = result.TopFragmentedSegments!;
        for (int i = 1; i < topFragmented.Count; i++)
        {
            int cmp = topFragmented[i - 1].FragmentationPercent.CompareTo(topFragmented[i].FragmentationPercent);
            if (cmp == 0)
                topFragmented[i].FreeBytes.Should().BeLessThanOrEqualTo(topFragmented[i - 1].FreeBytes);
            else
                cmp.Should().BeGreaterThan(0);
        }

        IReadOnlyList<LargeObjectSnapshot> topLargeObjects = result.TopLargeObjects!;
        topLargeObjects.Count.Should().BeLessThanOrEqualTo(100, "LargeObjectIndex.bin caps at 100 captured entries");
        for (int i = 1; i < topLargeObjects.Count; i++)
            topLargeObjects[i].Size.Should().BeLessThanOrEqualTo(topLargeObjects[i - 1].Size);

        IReadOnlyList<LohTypeProfile> typeProfiles = result.TopLargeObjectTypes!;
        typeProfiles.Should().NotBeEmpty("a real IIS crash dump has LOH/POH-resident types");
        for (int i = 1; i < typeProfiles.Count; i++)
            typeProfiles[i].TotalBytes.Should().BeLessThanOrEqualTo(typeProfiles[i - 1].TotalBytes);

        _output.WriteLine(
            $"Segments={result.SegmentCount:N0} TotalBytes={result.TotalBytes:N0} FreeBytes={result.FreeBytes:N0} " +
            $"UsedBytes={result.UsedBytes:N0} FragmentationPercent={result.FragmentationPercent:F2}% " +
            $"FreeBlockCount={result.FreeBlockCount:N0} LargestFreeBlock={result.LargestFreeBlock:N0} " +
            $"CapturedLargeObjects={topLargeObjects.Count:N0} TypeProfiles={typeProfiles.Count:N0}");
        if (typeProfiles.Count > 0)
            _output.WriteLine($"Largest LOH/POH type: {typeProfiles[0].TypeName} ({typeProfiles[0].TotalBytes:N0} bytes)");
    }
}
