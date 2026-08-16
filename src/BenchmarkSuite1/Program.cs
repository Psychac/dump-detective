using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

namespace BenchmarkSuite1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length is 2 or 3 && args[0] == "--single-index-run")
            {
                RunSingleIndexBuild(args[1], args.Length == 3 ? args[2] : null);
                return;
            }

            if (args.Length == 2 && args[0] == "--single-string-analyzer-run")
            {
                RunSingleStringAnalyzer(args[1]);
                return;
            }

            // Job iteration counts are enforced via [Config(AnalyzerBenchmarkIterationConfig)] on
            // AnalyzerBenchmarkBase (type-level config wins over the assembly-level mutator injected
            // by BenchmarkProfilerAgentConfig). No mutator override needed here.
            var config = DefaultConfig.Instance
                .AddExporter(JsonExporter.Full);

            _ = BenchmarkSwitcher
                .FromAssembly(typeof(Program).Assembly)
                .Run(args, config);
        }

        // One-shot (no warmup, no repeated iterations) baseline measurement of the disk-backed
        // Phase 1 index build, for dumps large enough that BenchmarkDotNet's multi-iteration jobs
        // are too costly to run repeatedly.
        // cacheDir, when supplied, redirects the index build to a scratch location instead of the
        // dump-colocated cache.bin — used by the §D5 incremental-build-cost measurement
        // (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md) to force a fresh build
        // without touching a real, already-populated cache.bin next to the dump. DD_SKIP_FORWARD_INDEX_BUILD
        // is read once into a static readonly field at type load, so comparing with/without requires
        // two separate process invocations (set the env var before launching, not mid-run).
        private static void RunSingleIndexBuild(string dumpPath, string? cacheDir)
        {
            if (!File.Exists(dumpPath))
                throw new InvalidOperationException($"Dump file not found: {dumpPath}");

            var fileInfo = new FileInfo(dumpPath);
            Console.WriteLine($"Dump: {dumpPath} ({fileInfo.Length / (1024.0 * 1024 * 1024):F2} GB)");
            Console.WriteLine($"DD_SKIP_FORWARD_INDEX_BUILD={Environment.GetEnvironmentVariable("DD_SKIP_FORWARD_INDEX_BUILD") ?? "(unset)"}");

            if (!string.IsNullOrEmpty(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                DumpIndexPaths.ResolveCacheDirectory(dumpPath, cacheDir);
                Console.WriteLine($"Cache dir (redirected): {cacheDir}");
            }

            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            var writer = new DiskBackedObjectIndexWriter();
            var sw = Stopwatch.StartNew();
            HeapIndexBuildResult result = writer.Build(heap, cancellationToken: default, progress: null, dumpPath: dumpPath);
            sw.Stop();

            Console.WriteLine($"ObjectCount: {result.ObjectCount}");
            Console.WriteLine($"Reported Elapsed: {result.Elapsed}");
            Console.WriteLine($"Wall-clock Elapsed (incl. dump load): {sw.Elapsed}");
            Console.WriteLine($"StorageKind: {result.StorageKind}");
            Console.WriteLine($"Peak WorkingSet: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024):F0} MB");
        }

        // One-shot measurement of StringAnalyzer alone, run through the real production path
        // (AnalysisPipeline.ExecuteAsync -> RunSharedScans -> HeapIndexScanDispatcher), which is
        // required for the Phase 1 precomputed StringFieldIndicesByMethodTable map to actually be
        // consumed. Calling StringAnalyzer.AnalyzeAsync directly (as AnalyzerBenchmarkBase does)
        // would skip BeforeHeapIndexScan/OnHeapEntry entirely and not exercise this path.
        private static void RunSingleStringAnalyzer(string dumpPath)
        {
            if (!File.Exists(dumpPath))
                throw new InvalidOperationException($"Dump file not found: {dumpPath}");

            Console.WriteLine($"Dump: {dumpPath}");

            using DataTarget dataTarget = DataTarget.LoadDump(dumpPath);
            ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
            ClrHeap heap = runtime.Heap;

            var cache = new HeapAnalysisCache();
            var indexSw = Stopwatch.StartNew();
            HeapIndexBuildResult indexResult = ((IHeapIndexBuilder)cache).PrebuildHeapIndex(
                heap, dumpPath, cancellationToken: default, progress: null);
            indexSw.Stop();
            Console.WriteLine($"Phase 1 index build: {indexSw.Elapsed} (ObjectCount={indexResult.ObjectCount}, StringFieldIndicesByMethodTable={(indexResult.StringFieldIndicesByMethodTable?.Count.ToString() ?? "null (cache hit)")})");

            var diagnostics = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };
            var context = new RuntimeAnalysisContext
            {
                Runtime = runtime,
                Cache = cache,
                AnalysisOptions = new AnalysisOptions
                {
                    MemoryLeak = new RetentionOptions(),
                    ReferenceChain = new ReferenceChainOptions(),
                    EventLeak = new EventLeakOptions(),
                    Diagnostics = diagnostics,
                },
                Diagnostics = diagnostics,
                DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
            };

            var pipeline = new AnalysisPipeline(
                new IAnalyzer[] { new StringAnalyzer() },
                new FindingGenerationPipeline(Array.Empty<IFindingGenerator>()));

            var sw = Stopwatch.StartNew();
            var results = pipeline.ExecuteAsync(context, CancellationToken.None).GetAwaiter().GetResult();
            sw.Stop();

            Console.WriteLine($"StringAnalyzer pipeline pass (shared scan + AnalyzeAsync): {sw.Elapsed}");
            Console.WriteLine($"Results: {results.Count}");
            Console.WriteLine($"Peak WorkingSet: {Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024):F0} MB");
        }
    }
}
