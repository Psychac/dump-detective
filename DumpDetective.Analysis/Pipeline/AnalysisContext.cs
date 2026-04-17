using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Pipeline
{
    internal interface IAnalyzer
    {
        string Name { get; }
        AnalyzerExecutionResult Execute(AnalysisContext context);
    }

    internal sealed record AnalyzerExecutionResult(
        IReadOnlyList<InsightFinding> Findings,
        AnalyzerDomainResult? DomainResult = null)
    {
        public static AnalyzerExecutionResult Empty { get; } = new([]);
    }

    internal class AnalysisContext
    {
        public required ClrRuntime Runtime { get; init; }
        public required ClrHeap Heap { get; init; }
        public required HeapAnalysisCache Cache { get; init; }
    }
}


