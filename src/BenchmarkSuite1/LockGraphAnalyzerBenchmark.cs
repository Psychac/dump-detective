using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    // Benchmarks LockGraphAnalyzerLegacyAdapter, not LockGraphAnalyzer directly, since 2026-09-11
    // (thread-domain quartet Batch 1) — LockGraphAnalyzer now implements the SDK's capability-scoped
    // Sdk.Analysis.IAnalyzer, not Core.Abstractions.IAnalyzer, so it no longer satisfies
    // AnalyzerBenchmarkBase<T>'s `where T : IAnalyzer, new()` constraint on its own. Calling
    // AnalyzeAsync directly here (not through AnalysisPipeline) means the shared thread-stack scan
    // never runs, so the adapter falls back to a live RuntimeThreadQuery — see
    // docs/refactor/modularity/phase-1-thread-quartet-plan.md.
    [MemoryDiagnoser]
    public class LockGraphAnalyzerBenchmark : AnalyzerBenchmarkBase<LockGraphAnalyzerLegacyAdapter>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeLockGraph()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }
    }
}
