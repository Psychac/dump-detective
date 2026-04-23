using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Abstractions;
using System;

namespace BenchmarkSuite1
{
    [MemoryDiagnoser]
    internal class MemoryAnalyzerBenchmark : AnalyzerBenchmarkBase<MemoryAnalyzer>
    {
        [Benchmark]
        public object AnalyzeMemory()
        {
            if (Heap == null || Cache == null)
                throw new InvalidOperationException("Benchmark not properly initialized.");
            return Analyzer.Analyze(Heap, (DumpDetective.Core.Abstractions.IHeapAnalysisCache)Cache);
        }
    }
}
