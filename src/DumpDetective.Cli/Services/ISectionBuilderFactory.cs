using DumpDetective.Reporting.Abstractions;

namespace DumpDetective.Cli.Services;

internal interface ISectionBuilderFactory
{
    IReadOnlyList<IAnalyzerSectionBuilder> CreateBuilders();
}
