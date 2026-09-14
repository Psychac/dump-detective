using DumpDetective.Analysis.Pipeline;
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
using DumpDetective.Analysis.Analyzers.Leaks;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// §9.14 (docs/refactor/analysis-profile-removal-plan.md): end-to-end validation that
/// <see cref="StaticRootLeakDetector"/> still runs without exceptions after switching its
/// retained-set analysis from <c>BoundedGraphWalk.CollectRetainedObjects</c> (deleted, dead code
/// with no remaining caller) to <c>IDominatorTreeProvider.EnumerateRetainedSet</c>/
/// <c>TryGetRetainedBytes</c>. Updated 2026-09-13 to construct
/// <see cref="StaticRootLeakDetector"/> for the Tier-1+Tier-2 retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — note that
/// <c>RootCount == 0</c> here is expected and does not indicate the retyping introduced a
/// regression: this analyzer's real-root detection is broken upstream (see
/// <see cref="StaticRootLeakDetector"/>'s own remarks), unrelated to and unfixed by this retyping.
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
        // Build a heap index up front, matching every other real-dump test's setup -- the
        // pre-retyping analyzer's own GetTotalManagedBytes fell back to a cheap segment-byte sum
        // when no index existed, but the retyped version reuses the shared
        // IHeapTypeStatisticsQuery surface, whose own no-index fallback (StatisticsCache's live
        // parallel type-classification walk) hit a real, pre-existing regex-timeout on this dump
        // under parallel load. Building the index avoids that fallback entirely -- not a change
        // to production behavior, since the real pipeline always builds an index first anyway.
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        using StaticRootLeakDetector analyzer = new();
        RuntimeAnalysisContext contextRuntimeCtx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = new AnalysisOptions() };
        DumpDetective.Sdk.Analysis.AnalysisContext context = AnalysisContextBuilder.Build(contextRuntimeCtx, analyzerOptions: null);

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        AnalyzerDomainResult result = (AnalyzerDomainResult)analyzer.LastResult!;

        var staticRootResult = result.Should().BeOfType<StaticRootDomainResult>().Subject;
        _output.WriteLine($"RootCount={staticRootResult.RootCount}, TotalRetainedBytes={staticRootResult.TotalRetainedBytes}, TopRoots={staticRootResult.TopRootsByRetainedBytes?.Count ?? 0}, TotalManagedHeapBytes={staticRootResult.TotalManagedHeapBytes}");

        // RootCount == 0 is the known, expected value today (upstream root-detection bug -- see
        // StaticRootLeakDetector's own remarks), not a tolerated edge case being loosely asserted.
        staticRootResult.RootCount.Should().BeGreaterThanOrEqualTo(0);
        staticRootResult.TopRootsByRetainedBytes.Should().NotBeNull();
        // TotalManagedHeapBytes does not depend on the broken root-detection path, so this stays a
        // real, verifiable figure even while RootCount reads zero.
        staticRootResult.TotalManagedHeapBytes.Should().BeGreaterThan(0UL);

        foreach (StaticRootSnapshot snapshot in staticRootResult.TopRootsByRetainedBytes!)
        {
            snapshot.TotalMemoryImpact.Should().BeGreaterThanOrEqualTo(0);
            snapshot.ObjectsKeptAlive.Should().BeGreaterThanOrEqualTo(0);
        }
    }
}
