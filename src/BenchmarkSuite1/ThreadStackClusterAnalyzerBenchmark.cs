using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    // Benchmarks ThreadStackClusterAnalyzerLegacyAdapter, not ThreadStackClusterAnalyzer directly,
    // since 2026-09-11 (thread-domain quartet Batch 2) — see LockGraphAnalyzerBenchmark's comment and
    // docs/refactor/modularity/phase-1-thread-quartet-plan.md.
    [MemoryDiagnoser]
    public class ThreadStackClusterAnalyzerBenchmark : AnalyzerBenchmarkBase<ThreadStackClusterAnalyzerLegacyAdapter>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeThreadStackClusters()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }
    }
}
