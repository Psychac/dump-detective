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
/// Real-dump verification for the Phase 1 retyping pilot
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md step 3, step 5). The
/// synthetic self-attached-heap characterization test
/// (<c>GCGenerationAnalyzerRetypingCharacterizationTests</c>) proves the arithmetic against
/// hand-crafted fixture data; this proves the full real pipeline — real dump load, a real
/// <c>HeapAnalysisCache.PrebuildHeapIndex</c> scan (hundreds of thousands of distinct types), real
/// <c>ClrType</c> name resolution — runs end to end through <see cref="GCGenerationAnalyzerLegacyAdapter"/>
/// without throwing and produces internally consistent output.
/// </summary>
public sealed class GCGenerationAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public GCGenerationAnalyzerRealDumpTests(ITestOutputHelper output)
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

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        AnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };

        using GCGenerationAnalyzerLegacyAdapter adapter = new();
        var result = (GCGenerationDomainResult)await adapter.AnalyzeAsync(context, CancellationToken.None);

        result.AnalyzerName.Should().Be("GC Generation Analysis");
        result.Category.Should().Be("GC");

        // A real dump with a prebuilt index takes the fast/exact path, not the coarse fallback.
        result.FallbackMode.Should().BeFalse();
        result.GenBytesAreApproximate.Should().BeFalse();

        result.TotalObjects.Should().BeGreaterThan(0);
        result.SohTotal.Should().Be(result.Gen0Bytes + result.Gen1Bytes + result.Gen2Bytes);
        (result.SohTotal + result.LohBytes).Should().BeGreaterThan(0);

        result.LohPercent.Should().BeInRange(0.0, 100.0);
        result.Gen2Pct.Should().BeInRange(0.0, 100.0);

        result.TopLohTypes.Should().NotBeNull();
        for (int i = 1; i < result.TopLohTypes.Count; i++)
            result.TopLohTypes[i].TotalBytes.Should().BeLessThanOrEqualTo(result.TopLohTypes[i - 1].TotalBytes);

        result.PerTypeGenerationProfiles.Should().NotBeNull();
        result.PerTypeGenerationProfiles.Should().NotBeEmpty("a real IIS crash dump has live managed types");
        foreach (TypeGenerationProfile profile in result.PerTypeGenerationProfiles!)
            profile.TypeName.Should().NotBeNullOrEmpty();

        long finalizableProfileGen2Count = 0;
        ulong finalizableProfileGen2Bytes = 0;
        foreach (TypeGenerationProfile profile in result.PerTypeGenerationProfiles!)
        {
            if (!profile.IsFinalizable) continue;
            finalizableProfileGen2Count += profile.Gen2Count;
            finalizableProfileGen2Bytes += profile.Gen2Bytes;
        }
        result.FinalizableGen2Count.Should().Be(finalizableProfileGen2Count);
        result.FinalizableGen2Bytes.Should().Be(finalizableProfileGen2Bytes);

        _output.WriteLine(
            $"TotalObjects={result.TotalObjects:N0} SohTotal={result.SohTotal:N0} LohBytes={result.LohBytes:N0} " +
            $"LohPercent={result.LohPercent:F2}% Gen2Pct={result.Gen2Pct:F2}% Types={result.PerTypeGenerationProfiles!.Count:N0} " +
            $"TopLohTypes={result.TopLohTypes.Count:N0} FinalizableGen2Count={result.FinalizableGen2Count:N0}");
        if (result.TopLohTypes.Count > 0)
            _output.WriteLine($"Largest LOH type: {result.TopLohTypes[0].TypeName} ({result.TopLohTypes[0].TotalBytes:N0} bytes)");
    }
}
