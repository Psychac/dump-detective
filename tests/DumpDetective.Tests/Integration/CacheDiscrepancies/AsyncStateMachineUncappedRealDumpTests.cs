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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M2: measures the cost of the suspend-state
/// histogram's second heap-index pass once <c>HistogramTopTypeLimit</c>/<c>HistogramInstanceCapPerType</c>/
/// <c>TypeCandidateLimit</c> are removed. The capped pass can early-exit once every tracked type's
/// per-type cap fills (<c>AsyncStateMachineAnalyzer.cs</c>'s <c>typesStillOpen == 0</c> break); fully
/// uncapped, no such early exit exists, so the pass must scan every entry in the object index. Opt-in
/// via <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class AsyncStateMachineUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void AsyncStateMachineAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-asyncsm-m2-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        var analyzer = new AsyncStateMachineAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AsyncStateMachineAnalysis = AsyncStateMachineAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (AsyncStateMachineDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"AsyncStateMachineAnalyzer, Balanced default (HistogramTopTypeLimit=10, HistogramInstanceCapPerType=1000, TypeCandidateLimit=200): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalStateMachines: {cappedResult.TotalStateMachines:N0}, TopStateMachineTypes: {cappedResult.TopStateMachineTypes.Count}, ScanLimited: {cappedResult.ScanLimited}");

        var uncappedOptions = new AsyncStateMachineAnalysisOptions
        {
            HistogramTopTypeLimit = int.MaxValue,
            HistogramInstanceCapPerType = int.MaxValue,
            TypeCandidateLimit = int.MaxValue,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { AsyncStateMachineAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (AsyncStateMachineDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"AsyncStateMachineAnalyzer, fully uncapped (all types x all instances): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalStateMachines: {uncappedResult.TotalStateMachines:N0}, TopStateMachineTypes: {uncappedResult.TopStateMachineTypes.Count}, ScanLimited: {uncappedResult.ScanLimited}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
