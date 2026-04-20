using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Services;

internal interface IAnalyzerFactory
{
    IReadOnlyList<IAnalyzer> CreateAnalyzers();
}
