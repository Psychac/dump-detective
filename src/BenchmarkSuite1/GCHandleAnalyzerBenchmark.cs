using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;
using DumpDetective.Analysis.Analyzers.Gc;

namespace BenchmarkSuite1
{
    // Benchmarks GCHandleAnalyzer, not GCHandleAnalyzer directly, since 2026-09-11
    // (Batch 7 of the Phase 1 retyping) — GCHandleAnalyzer now implements the SDK's capability-scoped
    // Sdk.Analysis.IAnalyzer, not Core.Abstractions.IAnalyzer, so it no longer satisfies
    // AnalyzerBenchmarkBase<T>'s `where T : IAnalyzer, new()` constraint on its own. See
    // docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
    [MemoryDiagnoser]
    public class GCHandleAnalyzerBenchmark : AnalyzerBenchmarkBase<GCHandleAnalyzer>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeGCHandles()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
            return Analyzer.LastResult!;
        }
    }
}
