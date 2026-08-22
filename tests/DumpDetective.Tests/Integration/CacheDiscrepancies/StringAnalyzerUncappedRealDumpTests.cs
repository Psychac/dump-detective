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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M5: memory profile of the string
/// fingerprint map once <c>MaxStringsToDedup</c> (50,000 by default — bounds how many strings get
/// read at all) and <c>MaxUniqueStringTracking</c> (200,000 — the genuine resident-bytes cap on the
/// fingerprint dictionary) are removed. Reports allocated-bytes delta and working-set delta, not
/// GC.GetTotalMemory (misleading if a gen2 collection happens mid-run — see the comment in
/// DominatorAnalyzerExactTreeRealDumpTests.cs). Opt-in via <see cref="DiscrepancyFactAttribute"/> —
/// loads a full real dump.
/// </summary>
public sealed class StringAnalyzerUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void StringAnalyzer_Uncapped_ReportsMemoryAndElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-string-m5-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        var analyzer = new StringAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { StringAnalysis = StringAnalysisOptions.Default },
        };

        long cappedAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        long cappedWsBefore = Environment.WorkingSet;
        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (StringDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        long cappedAllocAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"StringAnalyzer, Balanced default (MaxStringsToDedup=50000, MaxUniqueStringTracking=200000): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalStrings: {cappedResult.TotalStrings:N0}, StringsSampled: {cappedResult.StringsSampled:N0}, SampledUniquePatterns: {cappedResult.SampledUniquePatterns:N0}, SamplingCoverage: {cappedResult.SamplingCoverage:P2}, DeduplicationSkipped: {cappedResult.DeduplicationSkipped}");
        output.WriteLine($"  allocated: {(cappedAllocAfter - cappedAllocBefore):N0} bytes, WS {cappedWsBefore:N0} -> {Environment.WorkingSet:N0}");

        var uncappedOptions = new StringAnalysisOptions
        {
            MaxStringsToDedup = int.MaxValue,
            MaxUniqueStringTracking = int.MaxValue,
            DeduplicationStringCountThreshold = int.MaxValue,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { StringAnalysis = uncappedOptions },
        };

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        long uncappedAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        long uncappedWsBefore = Environment.WorkingSet;
        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (StringDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        long uncappedAllocAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"StringAnalyzer, fully uncapped (every string read, unbounded fingerprint map): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalStrings: {uncappedResult.TotalStrings:N0}, StringsSampled: {uncappedResult.StringsSampled:N0}, SampledUniquePatterns: {uncappedResult.SampledUniquePatterns:N0}, SamplingCoverage: {uncappedResult.SamplingCoverage:P2}, DeduplicationSkipped: {uncappedResult.DeduplicationSkipped}");
        output.WriteLine($"  allocated: {(uncappedAllocAfter - uncappedAllocBefore):N0} bytes, WS {uncappedWsBefore:N0} -> {Environment.WorkingSet:N0}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
