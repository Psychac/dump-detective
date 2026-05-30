using DumpDetective.Reporting.Abstractions;

namespace DumpDetective.Reporting.Services;

internal interface ISectionBuilderFactory
{
    IReadOnlyList<IAnalyzerSectionBuilder> CreateAnalyzerBuilders();
    IReadOnlyList<IReportSectionBuilder> CreateReportBuilders();
}