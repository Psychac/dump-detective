using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Base class for analyzer benchmarks. Handles dump loading, runtime/heap setup, and cache creation.
    /// Job configuration is centralized here: 0 warmup iterations, 3 measurement iterations.
    /// Setup mirrors DumpAnalysisService: loads dump, builds heap index, and constructs a fully
    /// populated AnalysisContext (including Options) so analyzers take the index-driven path.
    /// </summary>
    /// <remarks>
    /// [Config(AnalyzerBenchmarkIterationConfig)] is the authoritative source for WarmupCount/IterationCount.
    /// [SimpleJob] alone is insufficient because BenchmarkProfilerAgentConfig (assembly-level) injects a
    /// mutator with WarmupCount=1/IterationCount=5 that overwrites it. The type-level config is merged
    /// last in BenchmarkDotNet's global → assembly → type chain, so its mutator values win.
    /// </remarks>
    [Config(typeof(AnalyzerBenchmarkIterationConfig))]
    [SimpleJob(warmupCount: AnalyzerBenchmarkIterationConfig.WarmupCount, iterationCount: AnalyzerBenchmarkIterationConfig.IterationCount)]
    public abstract class AnalyzerBenchmarkBase<TAnalyzer> where TAnalyzer : IAnalyzer, new()
    {
        protected TAnalyzer Analyzer = default!;
        protected ClrRuntime? Runtime;
        protected ClrHeap? Heap;
        protected IHeapAnalysisCache? Cache;

        // Built once in Setup and reused across all iterations — avoids per-call allocation
        // and ensures the index-path is available to every analyzer.
        // Protected setter so subclasses can inject additional options after base.Setup().
        protected AnalysisContext? AnalysisContext { get; set; }

        private DataTarget? _dataTarget;

        /// <summary>
        /// Override to provide a custom dump path.
        /// </summary>
        protected virtual string GetDumpPath()
        {
            return Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
                ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp";
        }

        /// <summary>
        /// Override to provide a custom cache implementation. Returning null falls back to a
        /// fresh HeapAnalysisCache with the heap index pre-built during Setup.
        /// </summary>
        protected virtual IHeapAnalysisCache? CreateCache() => null;

        [GlobalSetup]
        public virtual void Setup()
        {
            Analyzer = new TAnalyzer();

            string dumpPath = GetDumpPath();
            if (!File.Exists(dumpPath))
                throw new InvalidOperationException($"Dump file not found: {dumpPath}");

            _dataTarget = DataTarget.LoadDump(dumpPath);
            Runtime = _dataTarget.ClrVersions[0].CreateRuntime();
            Heap = Runtime.Heap;

            // Use the subclass-provided cache or create a default HeapAnalysisCache.
            Cache = CreateCache() ?? new HeapAnalysisCache();

            // Mirror DumpAnalysisService: pre-build the heap index so analyzers take the
            // index-driven path (EnumerateIndexedEntries) instead of re-walking the heap.
            if (Cache is HeapAnalysisCache heapCache)
            {
                heapCache.PrebuildHeapIndex(
                    Heap,
                    dumpPath,
                    cancellationToken: default,
                    progress: null,
                    mode: HeapIndexPrebuildMode.Auto);
            }

            // Build context once — matches the Options dictionary DumpAnalysisService populates
            // so analyzers resolve their typed options rather than falling back to defaults.
            var memoryLeakOptions = new RetentionOptions();
            var referenceChainOptions = new ReferenceChainOptions();
            var eventLeakOptions = new EventLeakOptions();
            var diagnosticsOptions = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };

            AnalysisContext = new AnalysisContext
            {
                Runtime = Runtime,
                Cache = Cache,
                Diagnostics = diagnosticsOptions,
                DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
                Options = new Dictionary<Type, object?>
                {
                    [typeof(RetentionOptions)] = memoryLeakOptions,
                    [typeof(ReferenceChainOptions)] = referenceChainOptions,
                    [typeof(EventLeakOptions)] = eventLeakOptions,
                    [typeof(DiagnosticsOptions)] = diagnosticsOptions,
                }
            };

            OnSetup();
        }

        /// <summary>Override to perform additional setup after the base context is fully initialized.</summary>
        protected virtual void OnSetup() { }

        /// <summary>Override to perform cleanup before the base cleanup runs.</summary>
        protected virtual void OnCleanup() { }

        [GlobalCleanup]
        public virtual void Cleanup()
        {
            OnCleanup();
            AnalysisContext = null;
            Cache = null;
            Runtime = null;
            _dataTarget?.Dispose();
            _dataTarget = null;
        }
    }
}
