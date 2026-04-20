using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BenchmarkSuite1;

using CoreAnalysisContext = DumpDetective.Core.Abstractions.AnalysisContext;
using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;

[MemoryDiagnoser]
[ShortRunJob]
public class PipelineHotspotBenchmark
{
    private AnalysisPipeline _pipeline = null!;
    private PipelineAnalysisContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        IAnalyzer[] analyzers = Enumerable.Range(0, 16)
            .Select(i => (IAnalyzer)new SyntheticAnalyzer($"Analyzer-{i:00}", i))
            .ToArray();

        _pipeline = new AnalysisPipeline(analyzers);
        _context = new PipelineAnalysisContext
        {
            Runtime = null!,
            Heap = null!,
            Cache = new HeapAnalysisCache(),
            Diagnostics = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true },
            DiagnosticsOptions = new DiagnosticsOptions { ContinueOnAnalyzerFailure = true },
            MemoryLeakOptions = new MemoryLeakOptions(),
            ReferenceChainOptions = new ReferenceChainOptions(),
            EventLeakOptions = new EventLeakOptions(),
            DiagnosticsSink = NullAnalysisDiagnosticsSink.Instance
        };
    }

    [Benchmark(Description = "Pipeline - 16 analyzers all success")]
    public async Task<int> ExecutePipeline_AllSuccess()
    {
        IReadOnlyList<AnalyzerRunResult> results = await _pipeline.ExecuteAsync(_context, CancellationToken.None);
        return results.Count;
    }

    private sealed class SyntheticAnalyzer(string name, int order) : IAnalyzer
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Synthetic";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(CoreAnalysisContext context, CancellationToken cancellationToken)
        {
            GenericAnalyzerDomainResult result = new()
            {
                AnalyzerName = Name,
                Category = Category,
                Findings =
                [
                    new InsightFinding(
                        Analyzer: Name,
                        Category: Category,
                        Severity: FindingSeverity.Info,
                        Title: "Synthetic finding",
                        Evidence: "Synthetic evidence",
                        Recommendation: "Synthetic recommendation",
                        Tags: ["benchmark"]) 
                ],
                Metrics = new Dictionary<string, object?>
                {
                    ["objectScans"] = 100,
                    ["cacheHits"] = 80,
                    ["cacheMisses"] = 20
                }
            };

            return ValueTask.FromResult<AnalyzerDomainResult>(result);
        }
    }
}
