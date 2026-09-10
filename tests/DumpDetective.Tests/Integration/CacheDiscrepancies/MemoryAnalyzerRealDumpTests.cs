using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Phase 1 retyping batch
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). The self-attached-heap
/// characterization test (<c>MemoryAnalyzerRetypingCharacterizationTests</c>) only exercises the
/// no-index (live scan) path; this proves the real index-backed path — hundreds of thousands of
/// distinct types, real module-name resolution, real segment layout — against a large real dump.
/// </summary>
public sealed class MemoryAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public MemoryAnalyzerRealDumpTests(ITestOutputHelper output)
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
        ulong expectedCommitted = 0;
        foreach (ClrSegment segment in heap.Segments)
        {
            expectedSegmentCount++;
            expectedCommitted += RangeLength(segment.CommittedMemory);
        }

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using MemoryAnalyzerLegacyAdapter adapter = new();
        var result = (MemoryDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("Memory Analysis");
        result.UniqueTypes.Should().BeGreaterThan(0, "a real IIS crash dump has many distinct live types");
        result.TopTypes.Should().HaveCount(result.UniqueTypes);

        ulong sumTypeBytes = 0;
        long sumTypeCount = 0;
        foreach (TypeSnapshot snapshot in result.TopTypes)
        {
            sumTypeBytes += snapshot.TotalBytes;
            sumTypeCount += snapshot.Count;
        }
        sumTypeBytes.Should().Be(result.TotalBytes);
        sumTypeCount.Should().Be(result.TotalObjects);

        result.LohPercent.Should().BeInRange(0.0, 100.0);
        result.Top1BytesPercent.Should().BeInRange(0.0, 100.0);
        result.Top5BytesPercent.Should().BeInRange(result.Top1BytesPercent, 100.0);
        result.Top10BytesPercent.Should().BeInRange(result.Top5BytesPercent, 100.0);
        result.MemoryPressureScore.Should().BeInRange(0.0, 100.0);
        result.LohFragmentationRatio.Should().BeInRange(0.0, 100.0);

        int walkedCount = 0;
        foreach (TypeSnapshot snapshot in result.TopTypes)
            if (snapshot.EstimatedRetainedBytes > 0) walkedCount++;
        walkedCount.Should().BeLessThanOrEqualTo(MemoryAnalyzer.TypesToWalkForRetainedSize);

        int actualSegmentCount = 0;
        ulong actualCommitted = 0;
        foreach (GCSegmentSummary summary in result.SegmentSummaries!)
        {
            actualSegmentCount += summary.SegmentCount;
            actualCommitted += summary.CommittedBytes;
        }
        actualSegmentCount.Should().Be(expectedSegmentCount);
        actualCommitted.Should().Be(expectedCommitted);

        int typesWithModuleName = 0;
        foreach (TypeSnapshot snapshot in result.TopTypes)
            if (!string.IsNullOrEmpty(snapshot.ModuleName)) typesWithModuleName++;
        typesWithModuleName.Should().BeGreaterThan(0, "a real dump's types resolve to real loaded modules");

        _output.WriteLine(
            $"UniqueTypes={result.UniqueTypes:N0} TotalBytes={result.TotalBytes:N0} TotalObjects={result.TotalObjects:N0} " +
            $"LohPercent={result.LohPercent:F2}% MemoryPressureScore={result.MemoryPressureScore:F1} " +
            $"LohFragmentationRatio={result.LohFragmentationRatio:F2}% TypesWithModuleName={typesWithModuleName:N0}/{result.UniqueTypes:N0} " +
            $"WalkedForRetainedSize={walkedCount}");
        if (result.TopTypes.Count > 0)
            _output.WriteLine($"Largest type: {result.TopTypes[0].TypeName} ({result.TopTypes[0].TotalBytes:N0} bytes)");
    }

    private static ulong RangeLength(MemoryRange range) => range.End >= range.Start ? range.End - range.Start : 0;
}
