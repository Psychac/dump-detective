using System.Diagnostics;
using System.Linq;

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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M1: measures the cost of removing
/// <c>ModuleAnalysisOptions.ModuleEnumerationLimit</c> — the Full preset's own comment warns
/// "the analysis time grows quickly with the number of modules." Runs <c>ModuleAnalyzer</c> twice
/// against the same prebuilt index: once with today's Balanced default (capped at 50 modules/domain)
/// and once fully uncapped (<see cref="int.MaxValue"/>, <see cref="TypeEnumerationMode.Full"/>) —
/// the delta is the real cost of exactness for this analyzer. Opt-in via
/// <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class ModuleAnalyzerUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void ModuleAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-module-m1-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        int totalModules = 0;
        foreach (ClrAppDomain domain in runtime.AppDomains)
            totalModules += domain.Modules.Count();
        output.WriteLine($"Total modules across all AppDomains: {totalModules:N0}");

        var analyzer = new ModuleAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ModuleAnalysis = ModuleAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (ModuleDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"ModuleAnalyzer, Balanced default (ModuleEnumerationLimit=50): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TopModulesByTypeCount count: {cappedResult.TopModulesByTypeCount?.Count ?? 0}");

        var uncappedOptions = new ModuleAnalysisOptions
        {
            ModuleEnumerationLimit = int.MaxValue,
            TypeEnumerationMode = TypeEnumerationMode.Full,
            PreferIndexOnly = false,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ModuleAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (ModuleDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"ModuleAnalyzer, fully uncapped (ModuleEnumerationLimit=int.MaxValue, TypeEnumerationMode=Full): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TopModulesByTypeCount count: {uncappedResult.TopModulesByTypeCount?.Count ?? 0}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
