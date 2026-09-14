using BenchmarkDotNet.Attributes;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;
using DumpDetective.Analysis.Analyzers.Leaks;

namespace BenchmarkSuite1
{
    [MemoryDiagnoser]
    public class EventLeakAnalyzerBenchmark : AnalyzerBenchmarkBase<EventLeakAnalyzer>
    {
        protected override IHeapAnalysisCache? CreateCache() => new DumpDetective.Analysis.Cache.HeapAnalysisCache();

        [Benchmark]
        public object AnalyzeEventLeaks()
        {
            if (AnalysisContext == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            Analyzer.AnalyzeAsync(AnalysisContext, default).GetAwaiter().GetResult();
            return Analyzer.LastResult!;
        }
    }
}
