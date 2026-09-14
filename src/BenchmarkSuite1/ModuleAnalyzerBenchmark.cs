using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using Microsoft.Diagnostics.Runtime;
using System;
using DumpDetective.Analysis.Analyzers.Runtime;

namespace BenchmarkSuite1
{
    /// <summary>
    /// Benchmarks for ModuleAnalyzer (retyped to the Tier-1+Tier-2 SDK contract, 2026-09-13 --
    /// see docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) covering:
    ///   A) Runtime-only path  — module inventory/AppDomain analysis without a heap index
    ///   B) Full cache path    — AnalyzeAsync with a prebuilt heap index (includes the
    ///                           ModuleName-grouped heap-footprint aggregation)
    ///
    /// The pre-retyping "ModuleAggregator.Aggregate in isolation" benchmark was removed along with
    /// ModuleAggregator itself -- the retyped analyzer replaced that ModuleId-joined aggregation with
    /// grouping IHeapTypeStatisticsQuery.GetTypeStatistics() by ModuleName (see ModuleAnalyzer's own
    /// remarks), so there's no longer an isolated aggregation step to benchmark separately from
    /// AnalyzeAsyncFull.
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
        }

        /// <summary>
        /// Runtime-only path: module inventory + AppDomain analysis, no heap index involved.
        /// </summary>
        [Benchmark(Baseline = true, Description = "Enumerate modules only (no index)")]
        public object AnalyzeModulesOnly()
        {
            if (_runtimeNoCacheRef is null)
                throw new InvalidOperationException("Runtime not initialized.");
            RuntimeAnalysisContext noCacheRuntimeContext = new() { Runtime = _runtimeNoCacheRef, Cache = null!, AnalysisOptions = new() };
            DumpDetective.Sdk.Analysis.AnalysisContext noCacheContext = AnalysisContextBuilder.Build(noCacheRuntimeContext, analyzerOptions: null);
            Analyzer.AnalyzeAsync(noCacheContext, default).GetAwaiter().GetResult();
            return Analyzer.LastResult!;
        }

        /// <summary>
        /// Full path: AnalyzeAsync with a prebuilt heap index, including the ModuleName-grouped
        /// heap-footprint aggregation. Baseline = false so BDN computes the ratio vs AnalyzeModulesOnly.
        /// </summary>
        [Benchmark(Description = "Full AnalyzeAsync (with heap index + aggregation)")]
        public object AnalyzeAsyncFull()
        {
            if (AnalysisContext is null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
            return Analyzer.LastResult!;
        }

        protected override void OnCleanup()
        {
            _runtimeNoCacheRef = null;
        }
    }
}
