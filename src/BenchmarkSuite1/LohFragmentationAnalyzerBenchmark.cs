using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    // Benchmarks LohFragmentationAnalyzerLegacyAdapter, not LohFragmentationAnalyzer directly, since
    // 2026-09-11 (Batch 6 of the Phase 1 retyping) — LohFragmentationAnalyzer now implements the
    // SDK's capability-scoped Sdk.Analysis.IAnalyzer, not Core.Abstractions.IAnalyzer, so it no
    // longer satisfies AnalyzerBenchmarkBase<T>'s `where T : IAnalyzer, new()` constraint on its own.
    // See docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
    [MemoryDiagnoser]
    public class LohFragmentationAnalyzerBenchmark : AnalyzerBenchmarkBase<LohFragmentationAnalyzerLegacyAdapter>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeLohFragmentation()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }
    }
}
