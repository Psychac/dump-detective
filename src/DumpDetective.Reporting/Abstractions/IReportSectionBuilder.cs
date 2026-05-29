using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Abstractions;

internal interface IReportSectionBuilder
{
    string SectionId { get; }
    string DisplayTitle { get; }
    int SortOrder { get; }

    /// <summary>
    /// Analyzer run names that contribute data to this cross-cutting section.
    /// Used by <c>NormalizeSectionContractSlots</c> to build combined provenance.
    /// </summary>
    IReadOnlyList<string> SourceAnalyzers { get; }

    bool CanBuild(AnalyzerResultSet results);

    AnalyzerDetailSection Build(AnalyzerResultSet results);
}