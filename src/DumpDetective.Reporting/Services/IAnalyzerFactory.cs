using DumpDetective.Core.Abstractions;

namespace DumpDetective.Reporting.Services;

internal interface IAnalyzerFactory
{
    IReadOnlyList<IAnalyzer> CreateAnalyzers();
}