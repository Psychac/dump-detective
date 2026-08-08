using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCHandleFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Handle Analysis";

    /// <summary>Configurable thresholds (P1-5) — can be set by DI container or tests.</summary>
    public int TotalHandlesWarningThreshold { get; init; } = 10000;
    public int PinnedHandleTargetsWarningThreshold { get; init; } = 1000;
    public ulong PinnedRetainedBytesWarningThreshold { get; init; } = 100 * 1024 * 1024;
    public double DependentUnresolvedPercentWarningThreshold { get; init; } = 50.0;

    public bool CanGenerate(AnalyzerDomainResult result) => result is GCHandleDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCHandleDomainResult r) return [];

        FindingSeverity severity = r.PinnedHandleTargets >= PinnedHandleTargetsWarningThreshold
            || r.TotalHandles >= TotalHandlesWarningThreshold
            ? FindingSeverity.Warning : FindingSeverity.Info;

        var findings = new List<InsightFinding>(capacity: 2)
        {
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
        };

        // P1-1: Add PinnedRetainedBytes threshold finding
        if (r.PinnedRetainedBytes > 0)
        {
            FindingSeverity pinnedBytesSeverity = r.PinnedRetainedBytes >= PinnedRetainedBytesWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            if (pinnedBytesSeverity == FindingSeverity.Warning)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "GC",
                    Severity: pinnedBytesSeverity,
                    Title: "High pinned retained bytes",
                    Evidence: $"Total pinned retained bytes: {FormatBytes(r.PinnedRetainedBytes)}.",
                    Recommendation: "Reduce pinned object lifetime or avoid pinning large objects to decrease heap fragmentation.",
                    Tags: ["gc-handle", "pinning", "memory-pressure"],
                    MetricValue: (double)r.PinnedRetainedBytes,
                    MetricUnit: "bytes"));
            }
        }

        if (r.DependentHandleCount > 0)
        {
            FindingSeverity dependentSeverity = r.DependentUnresolvedPercent >= DependentUnresolvedPercentWarningThreshold
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: dependentSeverity,
                Title: "Dependent-handle retention summary",
                Evidence: $"Dependent handles: {r.DependentHandleCount:N0}; resolved source->target edges: {r.DependentResolvedEdgeCount:N0}; unresolved targets: {r.DependentUnresolvedTargetCount:N0} ({r.DependentUnresolvedPercent:F1}%).",
                Recommendation: "Inspect dominant dependent-handle source/target pairs to identify hidden retention relationships.",
                Tags: ["dependent-handle", "retention", "conditionalweaktable"],
                MetricValue: r.DependentUnresolvedPercent,
                MetricUnit: "% unresolved-targets"));
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:F1} {sizes[order]}";
    }
}
