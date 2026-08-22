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
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M6: measures the cost of
/// <c>CountSohObjects = true</c>. Confirmed by reading <c>HeapTopologyAnalyzer.CountObjects</c> that
/// this is served by a fresh live <c>segment.EnumerateObjects()</c> ClrMD walk, not the disk-backed
/// object index — <c>HeapTopologyAnalyzer.Analyze</c> never takes <c>IHeapAnalysisCache</c> as a
/// parameter at all, so no Phase 1 index build is needed for this test. Opt-in via
/// <see cref="DiscrepancyFactAttribute"/> — loads a full real dump.
/// </summary>
public sealed class HeapTopologyUncappedRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void HeapTopologyAnalyzer_CountSohObjects_ReportsElapsedAgainstDefault()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();
        var analyzer = new HeapTopologyAnalyzer();

        var defaultContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { HeapTopology = HeapTopologyAnalysisOptions.Default },
        };

        var defaultStopwatch = Stopwatch.StartNew();
        var defaultResult = (HeapTopologyDomainResult)analyzer.AnalyzeAsync(defaultContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        defaultStopwatch.Stop();
        output.WriteLine($"HeapTopologyAnalyzer, Balanced default (CountSohObjects=false): {defaultStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalSegments: {defaultResult.TotalSegments:N0}, TotalCommittedBytes: {defaultResult.TotalCommittedBytes:N0}, SohSegmentCount: {defaultResult.SohSegmentCount:N0}");

        var exactContext = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions { HeapTopology = new HeapTopologyAnalysisOptions { CountSohObjects = true } },
        };

        var exactStopwatch = Stopwatch.StartNew();
        var exactResult = (HeapTopologyDomainResult)analyzer.AnalyzeAsync(exactContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        exactStopwatch.Stop();
        output.WriteLine($"HeapTopologyAnalyzer, CountSohObjects=true (full live SOH walk): {exactStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"  TotalSegments: {exactResult.TotalSegments:N0}, TotalCommittedBytes: {exactResult.TotalCommittedBytes:N0}, SohSegmentCount: {exactResult.SohSegmentCount:N0}");

        output.WriteLine($"Delta (CountSohObjects=true - default): {exactStopwatch.ElapsedMilliseconds - defaultStopwatch.ElapsedMilliseconds:N0} ms");
    }
}
