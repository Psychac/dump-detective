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

            var indexStopwatch = Stopwatch.StartNew();
            cache.PrebuildHeapIndex(heap, dumpPath, CancellationToken.None, progress: null);
            indexStopwatch.Stop();
            output.WriteLine($"Phase 1 index build (incl. §D5 forward index): {indexStopwatch.ElapsedMilliseconds:N0} ms");

            var logger = new TestOutputLogger<DominatorAnalyzer>(output);
            var analyzer = new DominatorAnalyzer(logger);

            var context = new AnalysisContext
            {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions { MemoryLeak = new RetentionOptions() },
            };

            long memoryBefore = GC.GetTotalMemory(forceFullCollection: false);
            var analyzerStopwatch = Stopwatch.StartNew();
            AnalyzerDomainResult result = analyzer.AnalyzeAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            analyzerStopwatch.Stop();
            long memoryAfter = GC.GetTotalMemory(forceFullCollection: false);

            output.WriteLine($"DominatorAnalyzer.AnalyzeAsync total (heuristic + exact-path attempt): {analyzerStopwatch.ElapsedMilliseconds:N0} ms");
            output.WriteLine($"Managed memory delta during analyzer run: {(memoryAfter - memoryBefore):N0} bytes");

            Assert.NotNull(result);

            if (result is DominatorDomainResult dominatorResult && dominatorResult.ExactRetainedBytesByTypeName is { } exactByType)
            {
                output.WriteLine($"§Report integration: exact retained bytes resolved for {exactByType.Count:N0}/{dominatorResult.TopDominatorTypes.Count:N0} top dominator types.");
                foreach (KeyValuePair<string, ulong> kvp in exactByType)
                    output.WriteLine($"  {kvp.Key}: {kvp.Value:N0} bytes (exact)");
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
