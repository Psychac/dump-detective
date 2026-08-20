using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase-1-only timing for Stage A's walk successors source (docs/analysis/phase1-redesigns/
/// dominator-tree-phase1-integration.md §8.8). Deliberately stops after
/// <c>PrebuildHeapIndex</c> and never runs <c>DominatorAnalyzer.AnalyzeAsync</c> (Phase 2) —
/// that's unrelated to what this comparison needs and, on a 25GB dump, adds enough wall-clock to
/// risk this project's real-dump tooling timeouts for no benefit. Set
/// <c>DD_USE_LOOSE_FILE_WALK_SUCCESSORS=1</c> before running to measure the loose-file reader
/// path instead of the default live ClrMD walk. Opt-in via <see cref="DiscrepancyFactAttribute"/>
/// — loads a full real dump (1GB-25GB+).
/// </summary>
public sealed class ForwardEdgeLooseFileReaderRealDumpTimingTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void PrebuildHeapIndex_Phase1Only_ReportsElapsed()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        // Same scratch-dir redirection as DominatorAnalyzerExactTreeRealDumpTests — the default
        // %TEMP% (C:) doesn't have enough free space for a 25GB dump's scratch index files.
        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-forward-loose-timing-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        // xunit's ITestOutputHelper buffers everything until the test finishes — worthless for
        // watching a run that takes many minutes on a large dump. Stream progress to a plain file
        // instead, flushed after every line, so it can be tailed live. Defaults next to the
        // scratch cache dir; override with DD_PROGRESS_LOG for a fixed, known path.
        string progressLogPath = Environment.GetEnvironmentVariable("DD_PROGRESS_LOG")
            ?? Path.Combine(scratchRoot, "dd-progress-" + Guid.NewGuid().ToString("N") + ".log");
        output.WriteLine($"Live progress log: {progressLogPath}");

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

        var indexStopwatch = Stopwatch.StartNew();
        HeapIndexBuildResult result = cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress);
        indexStopwatch.Stop();

        progressLog.WriteLine($"[{progressStopwatch.Elapsed:hh\\:mm\\:ss}] DONE: {indexStopwatch.ElapsedMilliseconds:N0} ms total");

        output.WriteLine($"Phase 1 index build (incl. §D5 forward index): {indexStopwatch.ElapsedMilliseconds:N0} ms");
        output.WriteLine($"Object count: {result.ObjectCount:N0}");
    }
}
