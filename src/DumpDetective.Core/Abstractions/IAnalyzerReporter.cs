using DumpDetective.Core.Models;

namespace DumpDetective.Core.Abstractions;

internal interface IAnalyzerReporter
{
    /// <summary>Internal key used to match against AnalyzerRunResult.AnalyzerName.</summary>
    string AnalyzerName { get; }

    /// <summary>Human-readable section title shown in formatted reports. Defaults to AnalyzerName if not overridden.</summary>
    string DisplayTitle => AnalyzerName;

    /// <summary>Controls ordering of detailed sections in the report output. Lower values appear first.</summary>
    int SortOrder => 100;

    bool CanHandle(AnalyzerDomainResult result);
    void Render(AnalyzerDomainResult result, IReportWriter writer);
}
