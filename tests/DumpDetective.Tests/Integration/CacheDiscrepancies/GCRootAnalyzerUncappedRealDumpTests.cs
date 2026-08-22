using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M4: measures the cost of removing
/// <c>PathSearchTopN</c> (25) — i.e. running root-path evidence for every candidate finding, not
/// just the top 25. The retained-bytes computation already skips the BFS entirely when the exact
/// dominator tree can answer it (§7.1 of dominator-tree-phase1-integration.md — "an exact hit skips
/// the BFS candidate list entirely"), but <c>BoundedGraphWalk.CollectForwardTypeNames</c> (the
/// forward-type-names-along-the-path walk, distinct from retained bytes) is still called
/// unconditionally per candidate and has *not* been re-pointed at the dominator tree
/// (<c>IDominatorTreeProvider.EnumerateRetainedSet</c> has no caller yet) — so this measures whether
/// that still-BFS-backed walk is affordable at full candidate count on a real dump, or whether it
/// genuinely needs the dominator-tree rewire before <c>PathSearchTopN</c> can be deleted. Requires
/// Stage B (the exact dominator tree) to be built during Phase 1, since <c>GCRootAnalyzer</c>
/// implements <c>IRequiresDominatorTreeIndex</c>. Opt-in via <see cref="DiscrepancyFactAttribute"/>
/// — loads a full real dump.
/// </summary>
public sealed class GCRootAnalyzerUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void GCRootAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-gcroot-m4-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();
        var gcRootAnalyzer = new GCRootAnalyzer();
        IReadOnlyList<IAnalyzer> activeAnalyzers = new IAnalyzer[] { gcRootAnalyzer };

        var indexStopwatch = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, activeAnalyzers, enableExactDominatorTree: true);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build (incl. Stage B exact dominator tree): {indexStopwatch.ElapsedMilliseconds:N0} ms");

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { GCRootAnalysis = GCRootAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (GCRootDomainResult)gcRootAnalyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"GCRootAnalyzer, Balanced default (PathSearchTopN=25): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalRoots: {cappedResult.TotalRoots:N0}, TopRootsBySeverity: {cappedResult.TopRootsBySeverity.Count}, RootPaths: {cappedResult.RootPaths.Count}, PathSearchCapped: {cappedResult.PathSearchCapped}");

        var uncappedOptions = new GCRootAnalysisOptions
        {
            TopSeverityLimit = int.MaxValue,
            PathSearchTopN = int.MaxValue,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { GCRootAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (GCRootDomainResult)gcRootAnalyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"GCRootAnalyzer, fully uncapped (root path for every finding): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalRoots: {uncappedResult.TotalRoots:N0}, TopRootsBySeverity: {uncappedResult.TopRootsBySeverity.Count}, RootPaths: {uncappedResult.RootPaths.Count}, PathSearchCapped: {uncappedResult.PathSearchCapped}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
