using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Reporting.Services;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M10: measures the dominator retention
/// provider's *concurrent resident* cost — not Stage B construction peak (already measured at
/// 6.42 GB in dominator-tree-phase1-integration.md §5), but the tree kept alive alongside every
/// other analyzer in the real module-order run (140 through 340) that queries it. Runs the true
/// production analyzer set (<c>DefaultAnalyzerFactory.CreateAnalyzers()</c>, not a hand-copied list)
/// through <c>AnalysisPipeline.ExecuteAsync</c> with Stage B enabled, sampling
/// <c>Environment.WorkingSet</c> on a background timer throughout the whole run to catch a transient
/// mid-run peak that a before/after snapshot could miss. Opt-in via
/// <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class DominatorConcurrentResidentMemoryRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public async Task FullPipeline_WithStageB_ReportsPeakWorkingSet()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-dominator-m10-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        // Real production analyzer list, in real module order — not a hand-copied subset, so this
        // stays in sync with the actual registry (DefaultAnalyzerFeatureModuleCatalog) automatically.
        var analyzers = new DefaultAnalyzerFactory().CreateAnalyzers();
        output.WriteLine($"Analyzer count: {analyzers.Count}");

        var indexStopwatch = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, analyzers, enableExactDominatorTree: true);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build (incl. Stage B exact dominator tree): {indexStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"Working set after Phase 1 (Stage B construction done): {Environment.WorkingSet:N0} bytes");

        var diagnostics = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };
        var context = new RuntimeAnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions
            {
                MemoryLeak = new RetentionOptions(),
                ReferenceChain = new ReferenceChainOptions(),
                EventLeak = new EventLeakOptions(),
                Diagnostics = diagnostics,
                Collection = CollectionAnalysisOptions.Default,
            },
            Diagnostics = diagnostics,
            DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
        };

        var pipeline = new AnalysisPipeline(analyzers, new FindingGenerationPipeline([]));

        long peakWorkingSet = Environment.WorkingSet;
        var samplerCts = new CancellationTokenSource();
        Task samplerTask = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                long ws = Environment.WorkingSet;
                if (ws > peakWorkingSet) peakWorkingSet = ws;
                try { await Task.Delay(200, samplerCts.Token); } catch (OperationCanceledException) { }
            }
        });

        var pipelineStopwatch = Stopwatch.StartNew();
        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);
        pipelineStopwatch.Stop();

        samplerCts.Cancel();
        await samplerTask;

        output.WriteLine($"Full pipeline ({results.Count} analyzers) elapsed: {pipelineStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"Peak working set during full pipeline run (Stage B resident throughout): {peakWorkingSet:N0} bytes ({peakWorkingSet / (1024.0 * 1024 * 1024):F2} GB)");
        output.WriteLine($"Working set at exit: {Environment.WorkingSet:N0} bytes");

        int failures = 0;
        foreach (AnalyzerRunResult r in results)
        {
            if (r.Status != AnalyzerExecutionStatus.Success)
            {
                failures++;
                output.WriteLine($"  Non-success: {r.AnalyzerName} — {r.Status}");
            }
        }
        output.WriteLine($"Analyzers not reporting Success: {failures}/{results.Count}");
    }
}
