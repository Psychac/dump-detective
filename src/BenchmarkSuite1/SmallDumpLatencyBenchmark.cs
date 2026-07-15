using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkSuite1;

/// <summary>
/// End-to-end cold-path latency on a small dump: load + heap index build + full pipeline,
/// all timed per iteration (unlike FullPipelineBenchmark, which excludes load/index-build).
/// Small dumps are opened interactively, so cold-start latency — not steady-state analyzer
/// throughput — is what the user actually feels. Acceptance gate for Tier 2 (single-file
/// container migration): guards against fixed per-dump overhead that a large-heap-oriented
/// format could introduce.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(AnalyzerBenchmarkIterationConfig))]
[SimpleJob(
    warmupCount: AnalyzerBenchmarkIterationConfig.WarmupCount,
    iterationCount: AnalyzerBenchmarkIterationConfig.IterationCount)]
public class SmallDumpLatencyBenchmark
{
    private string _dumpPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _dumpPath = Environment.GetEnvironmentVariable("DD_SMALL_BENCHMARK_DUMP")
            ?? @"D:\DUmps\Crash_Management_Agent-BALSTG\PID__21624__Date__03_23_2026__Time_06_03_26PM.dmp";

        if (!File.Exists(_dumpPath))
            throw new InvalidOperationException($"Dump file not found: {_dumpPath}");
    }

    [Benchmark(Description = "Small-dump end-to-end latency — load + index + full pipeline")]
    public async Task<int> RunEndToEnd()
    {
        using DataTarget dataTarget = DataTarget.LoadDump(_dumpPath);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;

        var cache = new HeapAnalysisCache();
        ((IHeapIndexBuilder)cache).PrebuildHeapIndex(
            heap,
            _dumpPath,
            cancellationToken: default,
            progress: null);

        var diagnostics = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };
        AnalysisOptions analysisOptions = new()
        {
            MemoryLeak = new RetentionOptions(),
            ReferenceChain = new ReferenceChainOptions(),
            EventLeak = new EventLeakOptions(),
            Diagnostics = diagnostics,
            Collection = CollectionAnalysisOptions.Default,
        };

        var context = new RuntimeAnalysisContext
        {
            Runtime = runtime,
            Cache = cache,
            AnalysisOptions = analysisOptions,
            Diagnostics = diagnostics,
            DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
        };

        var pipeline = new AnalysisPipeline(new IAnalyzer[]
        {
            new MemoryAnalyzer(),
            new GCGenerationAnalyzer(),
            new HeapTopologyAnalyzer(),
            new ModuleAnalyzer(),
            new CrashAnalyzer(),
            new HangAnalyzer(),
            new RetentionAnalyzer(),
            new StringAnalyzer(),
            new CollectionAnalyzer(),
            new StaticRootLeakDetector(),
            new ReferenceChainAnalyzer(),
            new GCHandleAnalyzer(),
            new DependentHandleAnalyzer(),
            new LohFragmentationAnalyzer(),
            new ThreadStackClusterAnalyzer(),
            new ThreadAnalyzer(),
            new LockGraphAnalyzer(),
            new EventLeakAnalyzer(),
        }, new FindingGenerationPipeline(Array.Empty<IFindingGenerator>()));

        IReadOnlyList<AnalyzerRunResult> results =
            await pipeline.ExecuteAsync(context, CancellationToken.None);
        return results.Count;
    }
}
