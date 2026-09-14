using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers.Infra;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Real-dump verification for the Tier-2 wave (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// proves <see cref="DbConnectionAnalyzer"/> produces an internally consistent result
/// against a real, large dump, including its first-ever real exercise of the new
/// <c>heap.root-paths</c> capability against production data.
/// </summary>
public sealed class DbConnectionAnalyzerRealDumpTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public DbConnectionAnalyzerRealDumpTests(ITestOutputHelper output)
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

        using DbConnectionAnalyzer adapter = new();
        new HeapIndexScanDispatcher().Run(cache, contextRuntimeCtx, [adapter], CancellationToken.None);

        context = ((ICustomizesAnalysisContext)adapter).BuildAnalysisContext(contextRuntimeCtx);

        await adapter.AnalyzeAsync(context, CancellationToken.None);
            var result = (DbConnectionDomainResult)adapter.LastResult!;

        result.AnalyzerName.Should().Be("DB Connection Analysis");

        if (!result.ConnectionsFound)
        {
            result.TotalConnections.Should().Be(0);
            _output.WriteLine("No DB connection objects found on this dump's heap -- empty result is correct.");
            return;
        }

        result.TotalConnections.Should().BeGreaterThan(0);
        int sumByState = result.OpenConnections + result.ClosedConnections + result.BrokenConnections
            + result.OtherConnections + result.UnknownStateConnections;
        sumByState.Should().Be(result.TotalConnections);

        result.Gen2OpenConnections.Should().BeLessThanOrEqualTo(result.OpenConnections);
        result.Gen0OpenConnections.Should().BeLessThanOrEqualTo(result.OpenConnections);

        int sumByType = 0;
        foreach (DbConnectionTypeSummary t in result.ByType) sumByType += t.TotalCount;
        sumByType.Should().Be(result.TotalConnections);

        for (int i = 1; i < result.ByType.Count; i++)
            result.ByType[i].TotalCount.Should().BeLessThanOrEqualTo(result.ByType[i - 1].TotalCount);

        foreach (PoolSummary pool in result.TopPools)
            pool.OpenConnections.Should().BeLessThanOrEqualTo(pool.TotalConnections);

        int withRootPath = 0;
        foreach (DbConnectionSnapshot snap in result.TopOpenConnections)
        {
            if (snap.RootPath is not null) withRootPath++;
        }

        _output.WriteLine(
            $"TotalConnections={result.TotalConnections} Open={result.OpenConnections} Closed={result.ClosedConnections} " +
            $"Broken={result.BrokenConnections} Other={result.OtherConnections} Unknown={result.UnknownStateConnections} " +
            $"Gen2Open={result.Gen2OpenConnections} Gen0Open={result.Gen0OpenConnections} " +
            $"TopOpenConnections={result.TopOpenConnections.Count} WithRootPath={withRootPath} TopPools={result.TopPools.Count}");
    }
}
