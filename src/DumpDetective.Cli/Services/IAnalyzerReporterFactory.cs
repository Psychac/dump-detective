using DumpDetective.Core.Abstractions;

namespace DumpDetective.Cli.Services;

internal interface IAnalyzerReporterFactory
{
    IReadOnlyList<IAnalyzerReporter> CreateReporters();
}
