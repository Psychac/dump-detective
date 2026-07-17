using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class DependentHandleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Dependent Handle Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is DependentHandleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not DependentHandleDomainResult r) return [];

        double unresolvedPct = r.DependentHandleCount == 0
            ? 0
            : r.UnresolvedTargetCount * 100.0 / r.DependentHandleCount;

        FindingSeverity severity = unresolvedPct >= 50 ? FindingSeverity.Warning : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: severity,
                Title: "Dependent-handle retention summary",
                Evidence: $"Dependent handles: {r.DependentHandleCount:N0}; resolved source->target edges: {r.ResolvedEdgeCount:N0}; unresolved targets: {r.UnresolvedTargetCount:N0} ({unresolvedPct:F1}%).",
                Recommendation: "Inspect dominant dependent-handle source/target pairs to identify hidden retention relationships.",
                Tags: ["dependent-handle", "retention", "conditionalweaktable"],
                MetricValue: unresolvedPct,
                MetricUnit: "% unresolved-targets")
        ];
    }
}
