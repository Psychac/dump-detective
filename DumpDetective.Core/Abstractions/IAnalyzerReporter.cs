using DumpDetective.Core.Models;
using System.IO;

namespace DumpDetective.Core.Abstractions;

internal interface IAnalyzerReporter
{
string AnalyzerName { get; }
bool CanHandle(AnalyzerDomainResult result);
void Render(AnalyzerDomainResult result, TextWriter writer);
}
