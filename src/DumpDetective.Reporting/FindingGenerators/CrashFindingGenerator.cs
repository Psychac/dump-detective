using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class CrashFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Crash Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is CrashDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not CrashDomainResult r) return [];

        if (r.TotalExceptions == 0)
        {
            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Stability",
                    Severity: FindingSeverity.Info,
                    Title: "No exception objects detected",
                    Evidence: "Crash analysis found no exception objects in the heap snapshot.",
                    Recommendation: "Validate dump type and capture settings if a crash was expected.",
                    Tags: ["crash", "exception", "stability"],
                    MetricValue: 0,
                    MetricUnit: "active-exceptions")
            ];
        }

        FindingSeverity severity = r.ActiveExceptions > 0 ? FindingSeverity.Critical
            : r.TotalExceptions > 0 ? FindingSeverity.Warning
            : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Stability",
                Severity: severity,
                Title: "Exception pressure in crash dump",
                Evidence: $"Total exceptions: {r.TotalExceptions:N0}; active thread exceptions: {r.ActiveExceptions:N0}; unique types: {r.ExceptionTypeCounts.Count:N0}.",
                Recommendation: r.ActiveExceptions > 0
                    ? "Prioritize active exception threads and top exception types for root-cause isolation."
                    : "Review top exception families for recurring fault paths.",
                Tags: ["crash", "exceptions", "threads"],
                MetricValue: r.ActiveExceptions,
                MetricUnit: "active-exceptions")
        ];
    }
}
