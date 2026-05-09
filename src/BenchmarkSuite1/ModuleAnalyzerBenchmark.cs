using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Benchmarks for ModuleAnalyzer covering:
    ///   A) Runtime-only path  — AnalyzeModules without a heap index (no aggregation)
    ///   B) Full cache path    — AnalyzeAsync with a prebuilt heap index (includes ModuleAggregator)
    ///   C) Aggregation only   — ModuleAggregator.Aggregate in isolation (no module enumeration)
    ///
    /// [MemoryDiagnoser]              — tracks Gen0/1/2 GC collections and managed byte allocations.
    /// [EventPipeProfiler(GcVerbose)] — captures GC events at high fidelity via EventPipe for
    ///                                  precise allocation attribution per benchmark invocation.
    ///
    /// VS DiagnosticsHub integration (CPUUsageDiagnoser, DotNetObjectAllocDiagnoser, etc.) is
    /// activated automatically by BenchmarkProfilerAgentConfig when the benchmarks are launched
    /// from the Visual Studio Performance Profiler tool window (Analyze > Performance Profiler).
    /// No explicit attribute is needed — the config file in this project handles it.
    /// </summary>
    [MemoryDiagnoser]
    [EventPipeProfiler(EventPipeProfile.GcVerbose)]
    public class ModuleAnalyzerBenchmark : AnalyzerBenchmarkBase<ModuleAnalyzer>
    {
        protected override IHeapAnalysisCache? CreateCache() => new HeapAnalysisCache();

        // Isolated runtime for the no-cache path so it doesn't reuse the index-bearing cache.
        private ClrRuntime? _runtimeNoCacheRef;

        protected override void OnSetup()
        {
            // base.Setup() has already run; Runtime, Heap, Cache and AnalysisContext are populated.
            _runtimeNoCacheRef = Runtime;

            // Register ModuleAnalysisOptions so the analyzer uses the options-path rather than defaults.
            if (AnalysisContext is not null)
            {
                var opts = new Dictionary<Type, object?>(AnalysisContext.Options)
                {
                    [typeof(ModuleAnalysisOptions)] = ModuleAnalysisOptions.Default
                };
                AnalysisContext = new AnalysisContext
                {
                    Runtime = AnalysisContext.Runtime,
                    Heap = AnalysisContext.Heap,
                    Cache = AnalysisContext.Cache,
                    Diagnostics = AnalysisContext.Diagnostics,
                    DiagnosticsSink = AnalysisContext.DiagnosticsSink,
                    Options = opts
                };
            }
        }

        /// <summary>
        /// Runtime-only path: module enumeration + string pooling + conflict detection.
        /// No heap index involved. Measures the cost of AnalyzeModules in isolation.
        /// </summary>
        [Benchmark(Baseline = true, Description = "Enumerate modules only (no index)")]
        public object AnalyzeModulesOnly()
        {
            if (_runtimeNoCacheRef is null)
                throw new InvalidOperationException("Runtime not initialized.");
            return Analyzer.Analyze(_runtimeNoCacheRef);
        }

        /// <summary>
        /// Full path: AnalyzeAsync with prebuilt heap index.
        /// Covers module enumeration + ModuleAggregator.Aggregate over TypeAggregateIndexEntry.
        /// Baseline = false so BDN computes the ratio vs AnalyzeModulesOnly.
        /// </summary>
        [Benchmark(Description = "Full AnalyzeAsync (with heap index + aggregation)")]
        public object AnalyzeAsyncFull()
        {
            if (AnalysisContext is null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }

        /// <summary>
        /// ModuleAggregator.Aggregate in isolation — measures the cost of folding
        /// TypeAggregateIndexEntry data into per-module stats (no module enumeration).
        /// </summary>
        [Benchmark(Description = "ModuleAggregator.Aggregate only")]
        public object AggregateOnly()
        {
            if (Cache is not HeapAnalysisCache hc || !hc.TryGetHeapIndex(out var index))
                throw new InvalidOperationException("No heap index available.");
            if (index.Modules is null)
                throw new InvalidOperationException("No module registry in index.");
            return ModuleAggregator.Aggregate(index.TypeAggregates, index.Modules, ModuleAnalysisOptions.Default);
        }

        protected override void OnCleanup()
        {
            _runtimeNoCacheRef = null;
        }
    }
}

