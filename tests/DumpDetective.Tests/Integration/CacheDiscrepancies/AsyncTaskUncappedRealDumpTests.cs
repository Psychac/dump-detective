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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M7: measures the cost of removing
/// <c>MaxTasksToScan</c>/<c>MaxTcsToScan</c>/<c>MaxVtsToScan</c>. <c>AsyncTaskAnalyzer.LoadTaskEntries</c>
/// prefers a Phase 1-built <c>TaskIndex.bin</c>/in-memory candidate array when present, falling back
/// to a live <c>heap.EnumerateObjects()</c> scan only when neither exists — this measures which path
/// actually executes once Phase 1's normal index build has run (no special participant registration).
/// Opt-in via <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class AsyncTaskUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void AsyncTaskAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-asynctask-m7-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        var analyzer = new AsyncTaskAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AsyncTaskAnalysis = AsyncTaskAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (AsyncTaskDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"AsyncTaskAnalyzer, Balanced default (MaxTasksToScan=50000, MaxTcsToScan=20000, MaxVtsToScan=20000): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalTasks: {cappedResult.TotalTasks:N0}");

        var uncappedOptions = new AsyncTaskAnalysisOptions
        {
            MaxTasksToScan = int.MaxValue,
            MaxTcsToScan = int.MaxValue,
            MaxVtsToScan = int.MaxValue,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AsyncTaskAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (AsyncTaskDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"AsyncTaskAnalyzer, fully uncapped (every task/TCS/VTS): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalTasks: {uncappedResult.TotalTasks:N0}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
