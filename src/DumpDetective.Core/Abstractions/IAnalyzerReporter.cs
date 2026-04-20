using DumpDetective.Core.Models;

namespace DumpDetective.Core.Abstractions;

internal interface IAnalyzerReporter
{
string AnalyzerName { get; }
bool CanHandle(AnalyzerDomainResult result);
void Render(AnalyzerDomainResult result, IReportWriter writer);
}
