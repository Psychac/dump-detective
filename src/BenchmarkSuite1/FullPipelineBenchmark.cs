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
/// End-to-end analysis pipeline benchmark.
/// Mirrors the real production run up to and including the analysis stage:
///   1. Load dump → DataTarget / ClrRuntime / ClrHeap
///   2. Prebuild heap index (Auto mode — same as BuildHeapIndexStage)
///   3. Build RuntimeAnalysisContext with all real options
///   4. Execute AnalysisPipeline with all 18 real analyzers
/// Report generation (FindingGenerationPipeline, ReportBuilderFacade, WriteOutputStage)
/// is intentionally excluded — this benchmark isolates analysis memory and CPU.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(AnalyzerBenchmarkIterationConfig))]
[SimpleJob(
    warmupCount: AnalyzerBenchmarkIterationConfig.WarmupCount,
    iterationCount: AnalyzerBenchmarkIterationConfig.IterationCount)]
public class FullPipelineBenchmark
{
    private DataTarget? _dataTarget;
    private ClrRuntime? _runtime;
    private ClrHeap? _heap;
    private HeapAnalysisCache? _cache;
    private RuntimeAnalysisContext? _context;
    private AnalysisPipeline? _pipeline;
    private string _dumpPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _dumpPath = Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
            ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\w3wp__BALTSTPRD__PID__9704__Date__03_24_2026__Time_03_49_19PM__68__Second_Chance_Exception_E0434352.dmp";

        if (!File.Exists(_dumpPath))
            throw new InvalidOperationException($"Dump file not found: {_dumpPath}");

        // Stage 1: load dump — identical to LoadDumpStage
        _dataTarget = DataTarget.LoadDump(_dumpPath);
        _runtime = _dataTarget.ClrVersions[0].CreateRuntime();
        _heap = _runtime.Heap;

        // Stage 2: build heap index — identical to BuildHeapIndexStage (Auto mode)
        _cache = new HeapAnalysisCache();
        ((IHeapIndexBuilder)_cache).PrebuildHeapIndex(
            _heap,
            _dumpPath,
            cancellationToken: default,
            progress: null,
            mode: HeapIndexPrebuildMode.Auto);

        // Stage 3: wire up context — identical to RunAnalyzersPipelineStage.BuildContext
        var diagnostics = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };
        _context = new RuntimeAnalysisContext
        {
            Runtime = _runtime,
            Heap = _heap,
            Cache = _cache,
            Diagnostics = diagnostics,
            DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
            Options = new Dictionary<Type, object?>
            {
                [typeof(RetentionOptions)] = new RetentionOptions(),
                [typeof(ReferenceChainOptions)] = new ReferenceChainOptions(),
                [typeof(EventLeakOptions)] = new EventLeakOptions(),
                [typeof(DiagnosticsOptions)] = diagnostics,
                [typeof(CollectionAnalysisOptions)] = CollectionAnalysisOptions.Default,
            }
        };

        // Stage 4: build pipeline with all real analyzers — same list as DefaultAnalyzerFactory
        _pipeline = new AnalysisPipeline(new IAnalyzer[]
        {
            new MemoryAnalyzer(),
            new GCGenerationAnalyzer(),
            new SegmentAnalyzer(),
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
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dataTarget?.Dispose();
    }

    /// <summary>
    /// Runs all 18 real analyzers against the real crash dump through the production pipeline.
    /// The context and cached index are reused across iterations so each iteration measures
    /// only the analysis cost — not dump loading or index building.
    /// </summary>
    [Benchmark(Description = "Full analysis pipeline — all 18 real analyzers")]
    public async Task<int> RunFullPipeline()
    {
        IReadOnlyList<AnalyzerRunResult> results =
            await _pipeline!.ExecuteAsync(_context!, CancellationToken.None);
        return results.Count;
    }
}
