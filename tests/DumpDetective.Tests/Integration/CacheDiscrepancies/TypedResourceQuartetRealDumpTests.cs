using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// docs/refactor/analysis-profile-removal-plan.md §11.4 M9: reports real per-type instance counts
/// for the typed-resource quartet's three heap-index-scan analyzers (DbConnection, WcfChannel,
/// HttpObject) to determine whether their `MaxStateSamplesPerType = 500` (a `private const`, not
/// config-exposed — see §9.32-9.34 preamble) actually binds on a real dump. Unlike the other M-items,
/// this constant cannot be overridden without editing source, so this test measures whether the cap
/// is even reached rather than measuring an uncapped run directly. Requires running these analyzers
/// as heap-index-scan participants via <see cref="HeapIndexScanDispatcher"/> before calling
/// <c>AnalyzeAsync</c> (unlike the simpler Phase-2-only analyzers in M1/M3/M4/M6/M8, which read
/// straight from <c>TypeAggregates</c>). Opt-in via <see cref="DiscrepancyFactAttribute"/> — loads a
/// full real dump.
/// </summary>
public sealed class TypedResourceQuartetRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void TypedResourceQuartet_ReportsInstanceCountsAgainstThe500Cap()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-typedresource-m9-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();
        var dbConnectionAnalyzer = new DbConnectionAnalyzer();
        var wcfChannelAnalyzer = new WcfChannelAnalyzer();
        var httpObjectAnalyzer = new HttpObjectAnalyzer();
        IReadOnlyList<IAnalyzer> activeAnalyzers = new IAnalyzer[] { dbConnectionAnalyzer, wcfChannelAnalyzer, httpObjectAnalyzer };

        var indexStopwatch = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, activeAnalyzers);
        indexStopwatch.Stop();
        output.WriteLine($"Phase 1 index build (with typed-resource quartet participants): {indexStopwatch.ElapsedMilliseconds:N0} ms");

        var context = new AnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = new AnalysisOptions(),
        };

        // BUG FOUND: passing these analyzers as `activeAnalyzers` to PrebuildHeapIndex does NOT
        // register them as heap-index-scan participants — that parameter is only consulted for
        // IRequiresDominatorTreeIndex (Stage B) gating (DiskBackedObjectIndexWriter.cs:216).
        // AnalysisPipeline.ExecuteAsync is what actually collects IHeapIndexScanParticipant/
        // IParallelHeapIndexScanParticipant analyzers and runs HeapIndexScanDispatcher before
        // calling AnalyzeAsync (AnalysisPipeline.cs:41-44) — calling AnalyzeAsync directly, as this
        // test originally did, skips that step entirely, so OnHeapEntry never populates candidate
        // state and every result reads back as zero regardless of what's actually on the heap.
        var dispatcherStopwatch = Stopwatch.StartNew();
        var scanParticipants = new IHeapIndexScanParticipant[] { dbConnectionAnalyzer, wcfChannelAnalyzer, httpObjectAnalyzer };
        new HeapIndexScanDispatcher().Run(cache, context, scanParticipants, CancellationToken.None);
        dispatcherStopwatch.Stop();
        output.WriteLine($"HeapIndexScanDispatcher.Run (populates participant state): {dispatcherStopwatch.ElapsedMilliseconds:N0} ms");

        var dbStopwatch = Stopwatch.StartNew();
        var dbResult = (DbConnectionDomainResult)dbConnectionAnalyzer.AnalyzeAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        dbStopwatch.Stop();
        output.WriteLine($"DbConnectionAnalyzer: {dbStopwatch.ElapsedMilliseconds:N0} ms, TotalConnections: {dbResult.TotalConnections:N0} (cap: 500/type), StateScanCapped: {dbResult.StateScanCapped}");

        var wcfStopwatch = Stopwatch.StartNew();
        var wcfResult = (WcfChannelDomainResult)wcfChannelAnalyzer.AnalyzeAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        wcfStopwatch.Stop();
        output.WriteLine($"WcfChannelAnalyzer: {wcfStopwatch.ElapsedMilliseconds:N0} ms, TotalChannels: {wcfResult.TotalChannels:N0} (cap: 500/type), StateScanCapped: {wcfResult.StateScanCapped}");

        var httpStopwatch = Stopwatch.StartNew();
        var httpResult = (HttpObjectDomainResult)httpObjectAnalyzer.AnalyzeAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        httpStopwatch.Stop();
        output.WriteLine($"HttpObjectAnalyzer: {httpStopwatch.ElapsedMilliseconds:N0} ms, TotalHttpObjects: {httpResult.TotalHttpObjects:N0} (cap: 500/type), InstanceScanCapped: {httpResult.InstanceScanCapped}");
    }
}
