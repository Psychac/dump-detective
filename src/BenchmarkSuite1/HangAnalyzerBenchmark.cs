using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;
using DumpDetective.Analysis.Analyzers.Threads;

namespace BenchmarkSuite1
{
    // Benchmarks HangAnalyzer, not HangAnalyzer directly, since 2026-09-12 --
    // HangAnalyzer now implements the SDK's capability-scoped Sdk.Analysis.IAnalyzer, not
    // Core.Abstractions.IAnalyzer, so it no longer satisfies AnalyzerBenchmarkBase<T>'s
    // `where T : IAnalyzer, new()` constraint on its own. See
    // docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
    [MemoryDiagnoser]
    public class HangAnalyzerBenchmark : AnalyzerBenchmarkBase<HangAnalyzer>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeHang()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
            return Analyzer.LastResult!;
        }
    }
}
