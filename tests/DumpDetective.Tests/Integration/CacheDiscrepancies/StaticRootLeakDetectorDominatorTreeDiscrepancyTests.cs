using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// §9.14 (docs/refactor/analysis-profile-removal-plan.md): end-to-end validation that
/// <see cref="StaticRootLeakDetector"/> still runs without exceptions after switching its
/// retained-set analysis from <c>BoundedGraphWalk.CollectRetainedObjects</c> (deleted, dead code
/// with no remaining caller) to <c>IDominatorTreeProvider.EnumerateRetainedSet</c>/
/// <c>TryGetRetainedBytes</c>.
/// </summary>
public sealed class StaticRootLeakDetectorDominatorTreeDiscrepancyTests
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    private readonly ITestOutputHelper _output;

    public StaticRootLeakDetectorDominatorTreeDiscrepancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DiscrepancyFact]
    public async Task StaticRootLeakDetector_RunsEndToEnd_AfterDominatorTreeRefactor()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath)) return;

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        HeapAnalysisCache cache = new();
        StaticRootLeakDetector analyzer = new();
        var context = new AnalysisContext { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions() };

        AnalyzerDomainResult result = await analyzer.AnalyzeAsync(context, CancellationToken.None);

        var staticRootResult = result.Should().BeOfType<StaticRootDomainResult>().Subject;
        _output.WriteLine($"RootCount={staticRootResult.RootCount}, TotalRetainedBytes={staticRootResult.TotalRetainedBytes}, TopRoots={staticRootResult.TopRootsByRetainedBytes?.Count ?? 0}");

        staticRootResult.RootCount.Should().BeGreaterThanOrEqualTo(0);
        staticRootResult.TopRootsByRetainedBytes.Should().NotBeNull();

        foreach (StaticRootSnapshot snapshot in staticRootResult.TopRootsByRetainedBytes!)
        {
            snapshot.TotalMemoryImpact.Should().BeGreaterThanOrEqualTo(0);
            snapshot.ObjectsKeptAlive.Should().BeGreaterThanOrEqualTo(0);
        }
    }
}
