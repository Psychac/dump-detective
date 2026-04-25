using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCHandleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Handle Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is GCHandleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCHandleDomainResult r) return [];

        FindingSeverity severity = r.PinnedHandleTargets >= 1000 || r.TotalHandles >= 10000
            ? FindingSeverity.Warning : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: severity,
                Title: "GC handle pressure summary",
                Evidence: $"Total handles: {r.TotalHandles:N0}; pinned-handle target count: {r.PinnedHandleTargets:N0}.",
                Recommendation: severity == FindingSeverity.Warning
                    ? "Inspect pinned-handle-heavy types and reduce long-lived pinning where possible."
                    : "Handle distribution appears within expected bounds for this snapshot.",
                Tags: ["gc-handle", "pinning", "retention"],
                MetricValue: r.TotalHandles,
                MetricUnit: "total-handles")
        ];
    }
}
