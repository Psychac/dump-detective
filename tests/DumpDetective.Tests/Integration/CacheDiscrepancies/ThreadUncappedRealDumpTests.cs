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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M8: measures the cost of unbounded stack
/// scans once <c>MaxFramesForThreadScan</c> (8) and <c>MaxStackRootsToCount</c> (256) are removed.
/// Uses <c>ThreadAnalyzer</c>'s documented direct-invocation fallback path ("tests, benchmarks") that
/// runs its own live thread categorization rather than requiring pipeline participant wiring. Opt-in
/// via <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class ThreadUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void ThreadAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-thread-m8-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        var analyzer = new ThreadAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ThreadAnalysis = ThreadAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (ThreadDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"ThreadAnalyzer, Balanced default (MaxFramesForThreadScan=8, MaxStackRootsToCount=256): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalThreadCount: {cappedResult.TotalThreadCount:N0}, AliveThreadCount: {cappedResult.AliveThreadCount:N0}");

        var uncappedOptions = new ThreadAnalysisOptions
        {
            MaxFramesForThreadScan = 100_000,
            MaxStackRootsToCount = 100_000,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ThreadAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (ThreadDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"ThreadAnalyzer, fully uncapped (MaxFramesForThreadScan=100000, MaxStackRootsToCount=100000): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalThreadCount: {uncappedResult.TotalThreadCount:N0}, AliveThreadCount: {uncappedResult.AliveThreadCount:N0}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
