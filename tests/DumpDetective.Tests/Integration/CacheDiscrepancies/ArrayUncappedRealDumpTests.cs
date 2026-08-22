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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M3: measures the cost of full element walks
/// over every qualifying sparse-array candidate (length &gt;= <c>SparseSampleMinLength</c>) once
/// <c>SampleStride</c> (walk every element, not every 100th), <c>SparseSampleLimit</c> (don't cap the
/// candidate set), and <c>TopSparseLimit</c> (a loop bound, not just a row limit — probing today stops
/// once 10 sparse arrays are found) are all removed. Opt-in via <see cref="DiscrepancyFactAttribute"/>
/// — loads a full real dump.
/// </summary>
public sealed class ArrayUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void ArrayAnalyzer_Uncapped_ReportsElapsedAgainstCappedBaseline()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-array-m3-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult indexResult = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build: {indexStopwatch.ElapsedMilliseconds:N0} ms, {indexResult.ObjectCount:N0} objects");

        var analyzer = new ArrayAnalyzer();

        var cappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ArrayAnalysis = ArrayAnalysisOptions.Default },
        };

        var cappedStopwatch = Stopwatch.StartNew();
        var cappedResult = (ArrayDomainResult)analyzer.AnalyzeAsync(cappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        cappedStopwatch.Stop();
        output.WriteLine($"ArrayAnalyzer, Balanced default (SampleStride=100, SparseSampleLimit=500, TopSparseLimit=10): {cappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalArrayObjects: {cappedResult.TotalArrayObjects:N0}, TopSparseArrays: {cappedResult.TopSparseArrays.Count}, ScanLimited: {cappedResult.ScanLimited}");

        // NOTE: TopSparseLimit=int.MaxValue throws OutOfMemoryException — ArrayAnalyzer.cs:241
        // passes it directly as a List<T> capacity (`new List<SparseArrayEntry>(options.TopSparseLimit)`),
        // which tries to allocate a 2-billion-element backing array. Using a large-but-sane value
        // instead; this is itself a real defect worth fixing regardless of this measurement (see
        // the note added to M3/§11.4).
        var uncappedOptions = new ArrayAnalysisOptions
        {
            SampleStride = 1,
            SparseSampleLimit = int.MaxValue,
            TopSparseLimit = 1_000_000,
        };
        var uncappedContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { ArrayAnalysis = uncappedOptions },
        };

        var uncappedStopwatch = Stopwatch.StartNew();
        var uncappedResult = (ArrayDomainResult)analyzer.AnalyzeAsync(uncappedContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        uncappedStopwatch.Stop();
        output.WriteLine($"ArrayAnalyzer, fully uncapped (every element of every qualifying array): {uncappedStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalArrayObjects: {uncappedResult.TotalArrayObjects:N0}, TopSparseArrays: {uncappedResult.TopSparseArrays.Count}, ScanLimited: {uncappedResult.ScanLimited}");

        output.WriteLine($"Delta (uncapped - capped): {uncappedStopwatch.ElapsedMilliseconds - cappedStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
