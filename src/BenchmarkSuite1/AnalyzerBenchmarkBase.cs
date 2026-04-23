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
    [SimpleJob(warmupCount: 0, iterationCount: 3)]
    public abstract class AnalyzerBenchmarkBase<TAnalyzer> where TAnalyzer : IAnalyzer, new()
    {
        protected TAnalyzer Analyzer = default!;
        protected ClrRuntime? Runtime;
        protected ClrHeap? Heap;
        protected IHeapAnalysisCache? Cache;

        // Built once in Setup and reused across all iterations — avoids per-call allocation
        // and ensures the index-path is available to every analyzer.
        protected AnalysisContext? AnalysisContext { get; private set; }

        private DataTarget? _dataTarget;

        /// <summary>
        /// Override to provide a custom dump path.
        /// </summary>
        protected virtual string GetDumpPath()
        {
            return Environment.GetEnvironmentVariable("DD_BENCHMARK_DUMP")
                ?? @"D:\DUmps\Crash_IIS_BALTSTPRD\w3wp__BALTSTPRD__PID__9704__Date__03_24_2026__Time_03_49_19PM__68__Second_Chance_Exception_E0434352.dmp";
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
            var memoryLeakOptions = new MemoryLeakOptions();
            var referenceChainOptions = new ReferenceChainOptions();
            var eventLeakOptions = new EventLeakOptions();
            var diagnosticsOptions = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true };

            AnalysisContext = new AnalysisContext
            {
                Runtime = Runtime,
                Heap = Heap,
                Cache = Cache,
                Diagnostics = diagnosticsOptions,
                DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance,
                Options = new Dictionary<string, object?>
                {
                    [nameof(MemoryLeakOptions)]    = memoryLeakOptions,
                    [nameof(ReferenceChainOptions)] = referenceChainOptions,
                    [nameof(EventLeakOptions)]      = eventLeakOptions,
                    [nameof(DiagnosticsOptions)]    = diagnosticsOptions,
                }
            };
        }

        [GlobalCleanup]
        public virtual void Cleanup()
        {
            AnalysisContext = null;
            Cache = null;
            Heap = null;
            Runtime = null;
            _dataTarget?.Dispose();
            _dataTarget = null;
        }
    }
}
