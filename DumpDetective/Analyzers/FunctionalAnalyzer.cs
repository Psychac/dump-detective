using DumpDetective.Models;

namespace DumpDetective.Analyzers
{
    internal sealed class FunctionalAnalyzer(string name, Func<AnalysisContext, AnalyzerOutput> fn) : IAnalyzer
    {
        public string Name => name;

        public AnalyzerExecutionResult Execute(AnalysisContext context)
        {
            var output = fn(context);
            return new AnalyzerExecutionResult(output.Findings, output.DomainResult);
        }
    }
}
