using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

// Targeted single-pass timing for the shared HeapIndexScanDispatcher pass: isolates raw disk-index
// enumeration cost from per-participant (analyzer) fan-out cost, then breaks the fan-out down
// per analyzer, one run each, no warmup.
// Opt-in only (DiscrepancyFactAttribute) — loads a real dump and prebuilds its heap index.
public sealed class HeapIndexScanDispatcherPerfTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void DispatcherPass_PerParticipantBreakdown_SinglePassEach()
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

        HeapAnalysisCache cache = new();
        Stopwatch buildSw = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        buildSw.Stop();
        output.WriteLine($"Index build/load: {buildSw.Elapsed.TotalSeconds:F2}s");

        AnalysisOptions analysisOptions = new();

        (string Name, Func<IAnalyzer> Factory)[] candidates =
        [
            ("CrashAnalyzer", () => new CrashAnalyzer()),
            ("HangAnalyzer", () => new HangAnalyzer()),
            ("AsyncTaskAnalyzer", () => new AsyncTaskAnalyzer()),
            ("EventLeakAnalyzer", () => new EventLeakAnalyzer()),
            ("DominatorAnalyzer", () => new DominatorAnalyzer()),
            ("CollectionAnalyzer", () => new CollectionAnalyzer()),
            ("DbConnectionAnalyzer", () => new DbConnectionAnalyzer()),
            ("StringAnalyzer", () => new StringAnalyzer()),
            ("WcfChannelAnalyzer", () => new WcfChannelAnalyzer()),
        ];

        foreach ((string name, Func<IAnalyzer> factory) in candidates)
        {
            IAnalyzer analyzer = factory();
            if (analyzer is not IHeapIndexScanParticipant participant)
            {
                output.WriteLine($"{name}: not a participant, skipped");
                continue;
            }

            RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };
            Stopwatch sw = Stopwatch.StartNew();
            new HeapIndexScanDispatcher().Run(cache, context, [participant], CancellationToken.None);
            sw.Stop();
            output.WriteLine($"{name}: {sw.Elapsed.TotalSeconds:F2}s");
        }
    }

    // Targeted single-pass timing for EventLeakAnalyzer's FULL AnalyzeAsync (scan + post-scan
    // work), to isolate whether the ~40s+ gap between the dispatcher-only scan time (~18s) and
    // the observed full-stage time (~60s) is in post-scan work (BuildTypeSizeMap /
    // StatisticsCache.GetOrBuildTypeStatistics, PopulateEvidence) rather than the scan itself.
    [DiscrepancyFact]
    public async Task EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown()
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

        HeapAnalysisCache cache = new();
        Stopwatch buildSw = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        buildSw.Stop();
        output.WriteLine($"Index build/load: {buildSw.Elapsed.TotalSeconds:F2}s");

        AnalysisOptions analysisOptions = new();
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        Stopwatch totalSw = Stopwatch.StartNew();
        try
        {
            await new EventLeakAnalyzer().AnalyzeAsync(context, CancellationToken.None);
        }
        finally
        {
            totalSw.Stop();
            Console.SetError(originalError);
        }

        output.WriteLine($"EventLeakAnalyzer.AnalyzeAsync TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s");
        output.WriteLine(captured.ToString());
    }

    // Targeted single-pass timing for DominatorAnalyzer's FULL AnalyzeAsync (scan + post-scan
    // work), mirroring EventLeakAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown, to find out
    // why the full analyzer stage (~30s observed) is more than the shared dispatcher-only scan.
    [DiscrepancyFact]
    public async Task DominatorAnalyzer_FullAnalyzeAsync_SinglePass_TimingBreakdown()
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

        HeapAnalysisCache cache = new();
        Stopwatch buildSw = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        buildSw.Stop();
        output.WriteLine($"Index build/load: {buildSw.Elapsed.TotalSeconds:F2}s");

        AnalysisOptions analysisOptions = new();
        RuntimeAnalysisContext context = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };

        TextWriter originalError = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        Stopwatch totalSw = Stopwatch.StartNew();
        try
        {
            await new DominatorAnalyzer().AnalyzeAsync(context, CancellationToken.None);
        }
        finally
        {
            totalSw.Stop();
            Console.SetError(originalError);
        }

        output.WriteLine($"DominatorAnalyzer.AnalyzeAsync TOTAL: {totalSw.Elapsed.TotalSeconds:F2}s");
        output.WriteLine(captured.ToString());
    }

    // Measures the full shared dispatcher pass wall-clock time with ALL participants, comparing
    // the auto-selected parallel path against a forced sequential baseline (maxWorkers=1).
    // Run this after promoting all analyzers to IParallelHeapIndexScanParticipant to confirm
    // the parallel path beats sequential on a real large dump.
    //
    // The two runs intentionally use freshly constructed analyzer instances each time and share
    // the same pre-built cache so the index is already memory-mapped and OS-page-cached, making
    // the comparison fair (no cold-start I/O difference between runs).
    [DiscrepancyFact]
    public void DispatcherPass_ParallelVsSequential_AllParticipants()
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

        HeapAnalysisCache cache = new();
        Stopwatch buildSw = Stopwatch.StartNew();
        cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
        buildSw.Stop();
        output.WriteLine($"Index build/load: {buildSw.Elapsed.TotalSeconds:F2}s");

        AnalysisOptions analysisOptions = new();

        IHeapIndexScanParticipant[] BuildParticipants() =>
        [
            new CrashAnalyzer(),
            new HangAnalyzer(),
            new AsyncTaskAnalyzer(),
            new EventLeakAnalyzer(),
            new DominatorAnalyzer(),
            new CollectionAnalyzer(),
            new DbConnectionAnalyzer(),
            new StringAnalyzer(),
            new WcfChannelAnalyzer(),
        ];

        output.WriteLine($"ProcessorCount: {Environment.ProcessorCount}");

        // ── Sequential baseline (maxWorkers=1 forces the single-threaded path) ──
        {
            RuntimeAnalysisContext ctx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };
            var sw = Stopwatch.StartNew();
            new HeapIndexScanDispatcher().Run(cache, ctx, BuildParticipants(), CancellationToken.None, maxWorkers: 1);
            sw.Stop();
            output.WriteLine($"Sequential (maxWorkers=1):  {sw.Elapsed.TotalSeconds:F2}s");
        }

        // ── Parallel (auto worker count) ──
        {
            RuntimeAnalysisContext ctx = new() { Runtime = runtime, Cache = cache, AnalysisOptions = analysisOptions };
            var sw = Stopwatch.StartNew();
            new HeapIndexScanDispatcher().Run(cache, ctx, BuildParticipants(), CancellationToken.None, maxWorkers: 0);
            sw.Stop();
            output.WriteLine($"Parallel   (maxWorkers=auto): {sw.Elapsed.TotalSeconds:F2}s");
        }
    }
}
