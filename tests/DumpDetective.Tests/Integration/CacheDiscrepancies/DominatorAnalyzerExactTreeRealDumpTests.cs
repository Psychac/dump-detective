using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Xunit;
using Xunit.Abstractions;

namespace DumpDetective.Tests.Integration.CacheDiscrepancies;

/// <summary>
/// Phase 6 (docs/analysis/phase1-redesigns/dominator-tree-implementation-plan.md) real-dump
/// validation of the Phase 5 "ship dark" wiring: runs the full pipeline (Phase 1 index build,
/// including the §D5 forward-edge index, then <c>DominatorAnalyzer.AnalyzeAsync</c>'s exact-path
/// attempt) against a real dump and captures the exact-vs-heuristic comparison log line plus
/// wall-clock/memory, feeding directly into the D7 drop-or-solve decision (see the design doc's
/// Open Question 5 and the implementation plan's Phase 6 section). Redirects the cache to a
/// scratch directory so it never touches the dump's real, already-populated <c>cache.bin</c>.
/// Opt-in via <see cref="DiscrepancyFactAttribute"/> — loads a full real dump (1GB-25GB+).
/// </summary>
public sealed class DominatorAnalyzerExactTreeRealDumpTests(ITestOutputHelper output)
{
    private static string DumpPath => Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
        ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";

    [DiscrepancyFact]
    public void ExactDominatorTree_RunsEndToEndAgainstRealDump()
    {
        string dumpPath = DumpPath;
        if (!File.Exists(dumpPath))
        {
            output.WriteLine($"Dump not found, skipping: {dumpPath}");
            return;
        }

        // The default %TEMP% (C:) doesn't have enough free space for a 25GB dump's scratch index
        // files — DD_SCRATCH_DIR lets this be redirected to a drive with room (see the 25GB-dump
        // run notes in the implementation plan's Phase 6 section).
        string scratchRoot = Environment.GetEnvironmentVariable("DD_SCRATCH_DIR") ?? Path.GetTempPath();
        string scratchCacheDir = Path.Combine(scratchRoot, "dd-dominator-phase6-" + Guid.NewGuid().ToString("N"));
        DumpIndexPaths.ResolveCacheDirectory(dumpPath, scratchCacheDir);

        try
        {
            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            var cache = new HeapAnalysisCache();

            // enableExactDominatorTree: true + a IRequiresDominatorTreeIndex analyzer in
            // activeAnalyzers is what flips buildStageB on (§10.3) — without both, this run would
            // silently skip Stage B entirely and every "exact" comparison below would be
            // heuristic-vs-heuristic, not exact-vs-heuristic.
            var dominatorAnalyzer = new DominatorAnalyzer(new TestOutputLogger<DominatorAnalyzer>(output));
            IReadOnlyList<IAnalyzer> activeAnalyzers = new IAnalyzer[] { dominatorAnalyzer };

            var indexStopwatch = Stopwatch.StartNew();
            cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null, activeAnalyzers, enableExactDominatorTree: true);
            indexStopwatch.Stop();
            output.WriteLine($"Phase 1 index build (incl. §D5 forward index, Stage B): {indexStopwatch.ElapsedMilliseconds:N0} ms");

            DominatorAnalyzer analyzer = dominatorAnalyzer;

            var context = new AnalysisContext
            {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions { MemoryLeak = new RetentionOptions() },
            };

            long memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long wsBefore = Environment.WorkingSet;
            var analyzerStopwatch = Stopwatch.StartNew();
            AnalyzerDomainResult result = analyzer.AnalyzeAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            analyzerStopwatch.Stop();
            long memoryAfter = GC.GetTotalMemory(forceFullCollection: false);
            long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

            output.WriteLine($"DominatorAnalyzer.AnalyzeAsync total (heuristic + exact-path attempt): {analyzerStopwatch.ElapsedMilliseconds:N0} ms");
            // GC.GetTotalMemory deltas are reported for comparison only: they measure net *heap size*
            // change, so a gen2 collection during this run makes them read low (even negative) while
            // the process working set climbs. Allocated-bytes and peak-WS are the honest numbers.
            output.WriteLine($"Managed heap-size delta (misleading — see comment): {(memoryAfter - memoryBefore):N0} bytes");
            output.WriteLine($"Total bytes ALLOCATED during analyzer run:        {(allocatedAfter - allocatedBefore):N0} bytes");
            output.WriteLine($"Working set {wsBefore:N0} -> {Environment.WorkingSet:N0} bytes");
            // GenerationInfo layout is gen0, gen1, gen2, LOH, POH — index 3 is the LOH. It matters
            // here because ChunkedBuffer's 64K-element chunks are 256KB (int) / 512KB (ulong), well
            // over the 85KB LOH threshold, so every chunk of every accumulator lands on the LOH.
            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            output.WriteLine($"Gen2 collections: {GC.CollectionCount(2):N0}, LOH size at exit: {gcInfo.GenerationInfo[3].SizeAfterBytes:N0} bytes");

            Assert.NotNull(result);

            if (result is DominatorDomainResult dominatorResult && dominatorResult.ExactRetainedBytesByTypeName is { } exactByType)
            {
                output.WriteLine($"§Report integration: exact retained bytes resolved for {exactByType.Count:N0}/{dominatorResult.TopDominatorTypes.Count:N0} top dominator types.");
                foreach (KeyValuePair<string, ulong> kvp in exactByType)
                    output.WriteLine($"  {kvp.Key}: {kvp.Value:N0} bytes (exact)");
            }

            // §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): with
            // Stage B built above, GCRootAnalyzer's ByKind rollup should now come back exact rather
            // than shallow-size-summed.
            var gcRootAnalyzer = new GCRootAnalyzer();
            var gcRootContext = new AnalysisContext
            {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions { MemoryLeak = new RetentionOptions() },
            };
            AnalyzerDomainResult gcRootResult = gcRootAnalyzer.AnalyzeAsync(gcRootContext, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            if (gcRootResult is GCRootDomainResult gcRoot)
            {
                output.WriteLine($"§12.1: GCRootAnalyzer ByKind ({gcRoot.ByKind.Count:N0} kinds):");
                foreach (RootKindSummary kind in gcRoot.ByKind)
                {
                    output.WriteLine($"  {kind.Kind}: {kind.Count:N0} roots, {kind.EstimatedRetainedBytes:N0} bytes " +
                        $"({(kind.IsExactRetainedBytes ? "exact" : "shallow-size fallback")}), {kind.PctOfManagedHeap:F1}% of heap");
                }
            }

            // §12.2: "how much would become collectible if this thread exited" — per-thread exact
            // retained bytes, cross-referencing RootStackThreadAttribution against the same
            // dominator tree Stage B just built.
            IThreadRetentionProvider? threadRetention = cache.TryGetThreadRetentionProvider();
            output.WriteLine($"§12.2: IThreadRetentionProvider {(threadRetention is null ? "unavailable" : "available")}.");
            if (threadRetention is not null)
            {
                int printed = 0;
                foreach (ClrThread thread in runtime.Threads)
                {
                    if (!thread.IsAlive)
                        continue;

                    if (threadRetention.TryGetRetainedBytesForThread(thread.OSThreadId, out ulong retainedBytes))
                    {
                        output.WriteLine($"  OSThread {thread.OSThreadId} (managed {thread.ManagedThreadId}): {retainedBytes:N0} bytes exact retained");
                        if (++printed >= 20)
                            break;
                    }
                }

                if (printed == 0)
                    output.WriteLine("  No live thread had any Stack-kind roots attributed to it.");
            }
        }
        finally
        {
            if (Directory.Exists(scratchCacheDir))
                Directory.Delete(scratchCacheDir, recursive: true);
        }
    }

    /// <summary>Minimal <see cref="ILogger{T}"/> that forwards every log line to xunit's test output.</summary>
    private sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            output.WriteLine($"[{logLevel}] {formatter(state, exception)}" + (exception is null ? "" : $"\n{exception}"));
        }
    }
}
