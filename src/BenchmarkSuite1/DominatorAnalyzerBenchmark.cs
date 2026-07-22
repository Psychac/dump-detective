using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    [MemoryDiagnoser]
    public class DominatorAnalyzerBenchmark : AnalyzerBenchmarkBase<DominatorAnalyzer>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeDominators()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
        }
    }
}
