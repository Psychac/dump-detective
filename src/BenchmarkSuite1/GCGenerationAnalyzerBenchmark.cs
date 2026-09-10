using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    // Benchmarks GCGenerationAnalyzerLegacyAdapter, not GCGenerationAnalyzer directly, since
    // 2026-09-10 — the Phase 1 retyping pilot; GCGenerationAnalyzer now implements the SDK's
    // capability-scoped Sdk.Analysis.IAnalyzer, not Core.Abstractions.IAnalyzer, so it no longer
    // satisfies AnalyzerBenchmarkBase<T>'s `where T : IAnalyzer, new()` constraint on its own. See
    // docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
    [MemoryDiagnoser]
    public class GCGenerationAnalyzerBenchmark : AnalyzerBenchmarkBase<GCGenerationAnalyzerLegacyAdapter>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeGCGeneration()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }
    }
}
