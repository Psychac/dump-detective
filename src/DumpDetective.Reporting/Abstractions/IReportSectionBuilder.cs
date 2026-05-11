using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Abstractions;

internal interface IReportSectionBuilder
{
    string SectionId { get; }
    string DisplayTitle { get; }
    int SortOrder { get; }

    bool CanBuild(AnalyzerResultSet results);

    AnalyzerDetailSection Build(AnalyzerResultSet results);
}