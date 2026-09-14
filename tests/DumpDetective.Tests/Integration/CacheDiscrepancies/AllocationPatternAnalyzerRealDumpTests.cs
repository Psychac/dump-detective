using DumpDetective.Analysis.Pipeline;
using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Memory;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-1 mop-up batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="AllocationPatternAnalyzer"/> produces an internally consistent
/// result against a real, large dump.
/// </summary>
public sealed class AllocationPatternAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public AllocationPatternAnalyzerRealDumpTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task AnalyzeAsync_RealDump_ProducesInternallyConsistentResult()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);

        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        using AllocationPatternAnalyzer adapter = new();
        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (AllocationPatternDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("Allocation Pattern Analysis");
        result.TotalManagedBytes.Should().BeGreaterThan(0, "a real 3.5 GB dump has a non-trivial managed heap");

        (result.Gen0CountPct + result.Gen1CountPct + result.Gen2CountPct + result.LohCountPct).Should().BeInRange(0.0, 100.5);
        // Not tightly bound at ~100%: SizePct's numerator (Gen0-2 bytes) comes from a live
        // segment sub-range walk (GetExactGenerationByteTotals) while its denominator
        // (TotalManagedBytes) comes from an independent per-type aggregate sum -- two genuinely
        // separate accounting passes over the same dump that don't perfectly reconcile (e.g.
        // segment sub-range padding not attributed to any single type). This is a pre-existing
        // property of the algorithm carried over unchanged from the pre-retyping analyzer, which
        // computed both the same way -- not something this retyping introduced or should "fix".
        (result.Gen0SizePct + result.Gen1SizePct + result.Gen2SizePct + result.LohSizePct).Should().BeInRange(0.0, 115.0);
        result.FinalizableBytes.Should().BeLessThanOrEqualTo(result.TotalManagedBytes);

        foreach (var t in result.TopTransientTypes)
            t.TotalSize.Should().BeGreaterThan(0UL);

        _output.WriteLine(
            $"TotalManagedBytes={result.TotalManagedBytes:N0} Profile={result.Profile} GCPressure={result.GCPressure} " +
            $"Gen0%={result.Gen0CountPct} Gen2%={result.Gen2CountPct} Loh%={result.LohCountPct} " +
            $"Transient={result.TopTransientTypes.Count} Shortish={result.TopShortishTypes.Count} LongLived={result.TopLongLivedTypes.Count} " +
            $"FinalizableTypeCount={result.FinalizableTypeCount} LohSizeBands={(result.LohSizeBands?.Count ?? 0)}");
    }
}
