using DumpDetective.Models;
using DumpDetective.Utilities;

namespace DumpDetective.Analyzers
{
    internal interface IAnalyzerReporter
    {
        string AnalyzerName { get; }
        bool CanHandle(AnalyzerDomainResult result);
        void Render(AnalyzerDomainResult result, OutputWriter writer);
    }
}
