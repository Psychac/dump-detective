using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// §10.8 measurement pass (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
/// runs Phase 1's index build with Stage B (<c>buildStageB</c>) actually enabled and
/// <c>DD_PERF_DOMINATOR_STAGEB=1</c> set, so a single pass emits everything §10.8 still needed a
/// real-dump number for — the unified walk's own wall-clock, each
/// <c>BuildAndPersistDominatorTree</c> sub-phase's wall-clock, the dominator child index's widest
/// row (hub-overflow sizing), and per-segment scratch-file address-monotonicity confirmation.
/// Deliberately stops after <c>PrebuildHeapIndex</c> — Phase 2 analyzer execution is unrelated to
/// what this pass measures. Opt-in via <see cref="DiscrepancyFactAttribute"/> — loads a full real
/// dump (1GB-25GB+); per this project's real-dump-test rule, run one dump at a time, in the
/// foreground, never in parallel.
/// </summary>
public sealed class DominatorStageBPerfMeasurementTests(ITestOutputHelper output)
{
    [DiscrepancyFact]
    public void StageB_3_3GbDump_MeasuresAllFourPendingItems()
    {
        string dumpPath = Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP_3_3GB")
            ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";
        RunStageBPerfPass(dumpPath, "dd-dominator-stageb-perf-33gb-");
    }

    [DiscrepancyFact]
    public void StageB_25_6GbDump_MeasuresAllFourPendingItems()
    {
        string dumpPath = Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP_25GB")
            ?? @"D:\DUmps\21-04\w3wp.exe_260421_175618.dmp";
        RunStageBPerfPass(dumpPath, "dd-dominator-stageb-perf-25gb-");
    }

    private void RunStageBPerfPass(string dumpPath, string scratchDirPrefix)
    {
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        // DiskBackedObjectIndexWriter's PerfLogDominatorStageB/ScratchFileObjectMetadataLookup's
        // VerifyMonotonicity are `static readonly` fields read once, lazily, on first touch of
        // their declaring type — setting this before either type is used anywhere in this process
        // is enough for it to take effect for this run.
        Environment.SetEnvironmentVariable("DD_PERF_DOMINATOR_STAGEB", "1");

        // Same scratch-dir redirection as DominatorAnalyzerExactTreeRealDumpTests — the default
        // %TEMP% (C:) doesn't have enough free space for a 25GB dump's scratch index files.
        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, scratchDirPrefix + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        // xunit's ITestOutputHelper buffers everything until the test finishes — worthless for
        // watching a run that takes many minutes on a large dump. Stream progress to a plain file
        // instead, flushed after every line, so it can be tailed live.
        string progressLogPath = Environment.GetEnvironmentVariable("DD_PROGRESS_LOG")
            ?? Path.Combine(scratchRoot, scratchDirPrefix + "progress-" + Guid.NewGuid().ToString("N") + ".log");
        output.WriteLine($"Live progress log: {progressLogPath}");
        output.WriteLine($"[PERF] lines (walk/phase timings, hub-overflow, monotonicity) go to stderr, not this log.");

        try
        {
            using var progressLog = new StreamWriter(progressLogPath, append: false) { AutoFlush = true };
            var progressStopwatch = Stopwatch.StartNew();
            var progress = new Progress<AnalyzerProgressReport>(r =>
                progressLog.WriteLine($"[{progressStopwatch.Elapsed:hh\\:mm\\:ss}] {r.Phase}"
                    + (string.IsNullOrWhiteSpace(r.Detail) ? "" : $" — {r.Detail}")
                    + $" (scanned {r.ScannedCount:N0})"));

            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            var cache = new HeapAnalysisCache();

            // DominatorAnalyzer implements both IRequiresReachableGraphIndex and
            // IRequiresDominatorTreeIndex — its mere presence in activeAnalyzers, combined with
            // enableExactDominatorTree: true, is what flips buildStageB on in
            // DiskBackedObjectIndexWriter.Build's gating (§10.3). Never AnalyzeAsync'd here — only
            // used as a marker-interface flag for the index build.
            IReadOnlyList<IAnalyzer> activeAnalyzers = new IAnalyzer[] { new DominatorAnalyzer() };

            var indexStopwatch = Stopwatch.StartNew();
            HeapIndexBuildResult result = cache.PrebuildHeapIndex(
                heap, dumpPath, CancellationToken.None, progress,
                activeAnalyzers, enableExactDominatorTree: true);
            indexStopwatch.Stop();

            progressLog.WriteLine($"[{progressStopwatch.Elapsed:hh\\:mm\\:ss}] DONE: {indexStopwatch.ElapsedMilliseconds:N0} ms total");

            output.WriteLine($"Phase 1 index build (Stage A + Stage B): {indexStopwatch.ElapsedMilliseconds:N0} ms");
            output.WriteLine($"Object count: {result.ObjectCount:N0}");
        }
        finally
        {
            if (Directory.Exists(scratchCacheDir))
                Directory.Delete(scratchCacheDir, recursive: true);
        }
    }
}
