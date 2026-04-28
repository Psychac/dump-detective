using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Abstractions;

internal interface IAnalyzerSectionBuilder
{
    string AnalyzerName { get; }           // Matches AnalyzerRunResult.AnalyzerName for routing
    string DisplayTitle => AnalyzerName;   // Human-readable section title
    int SortOrder => 100;                  // Controls order in report output

    bool CanHandle(AnalyzerDomainResult result);
    AnalyzerDetailSection Build(AnalyzerDomainResult result);
    // Returns pure structured data — no writer, no text formatting
}
