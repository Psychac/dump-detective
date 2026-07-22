using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class DominatorFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Dominator Analysis";

    public bool CanGenerate(AnalyzerDomainResult result) => result is DominatorDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not DominatorDomainResult r) return [];

        var findings = new List<InsightFinding>(capacity: 5);

        if (r.TopDominatorTypes.Count > 0)
        {
            TypeSnapshot top = r.TopDominatorTypes[0];
            FindingSeverity severity = top.EstimatedRetainedBytes >= 500UL * 1024 * 1024
                ? FindingSeverity.Critical
                : top.EstimatedRetainedBytes >= 100UL * 1024 * 1024
                    ? FindingSeverity.Warning
                    : FindingSeverity.Info;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: severity,
                Title: $"Dominant retention suspect: {top.TypeName}",
                Evidence: $"{top.TypeName} estimates {FormatHelper.FormatBytes(top.EstimatedRetainedBytes)} retained across {top.Count:N0} instances.",
                Recommendation: "Use the retention section and root paths to confirm why this type remains live.",
                Tags: ["dominator", "retained-bytes", top.TypeName],
                MetricValue: top.EstimatedRetainedBytes,
                MetricUnit: "bytes"));
        }

        if (r.HighlyReferencedObjectCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: r.HighlyReferencedObjectCount >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning,
                Title: "Highly referenced objects indicate retention hotspots",
                Evidence: $"{r.HighlyReferencedObjectCount:N0} objects with abnormally high incoming reference counts were detected.",
                Recommendation: "Inspect root paths and long-lived graphs retaining these objects.",
                Tags: ["retention", "references", "memory-leak"],
                MetricValue: r.HighlyReferencedObjectCount,
                MetricUnit: "objects"));
        }

        var topTypes = r.TopRetentionTypes ?? [];
        if (topTypes.Count > 0)
        {
            RetentionTypeSnapshot topType = topTypes[0];
            for (int i = 1; i < topTypes.Count; i++)
            {
                RetentionTypeSnapshot candidate = topTypes[i];
                if (candidate.TotalIncomingReferences > topType.TotalIncomingReferences)
                    topType = candidate;
            }

            if (topType.TotalIncomingReferences >= 1_000)
            {
                findings.Add(new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Retention",
                    Severity: topType.TotalIncomingReferences >= 10_000 ? FindingSeverity.Critical : FindingSeverity.Warning,
                    Title: "Dominant retained type hotspot detected",
                    Evidence: $"Type '{topType.TypeName}' contributes {topType.TotalIncomingReferences:N0} incoming references across " +
                              $"{topType.ObjectCount:N0} top hotspot object(s), footprint {FormatHelper.FormatBytes(topType.TotalBytes)}.",
                    Recommendation: "Inspect this type's root paths and owners, then reduce long-lived references or partition caches by scope.",
                    Tags: ["retention", "type-hotspot", "references"],
                    MetricValue: topType.TotalIncomingReferences,
                    MetricUnit: "references"));
            }
        }

        if (r.SkippedReferenceAddresses > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Diagnostics",
                Severity: FindingSeverity.Info,
                Title: "Reference tracking was capped",
                Evidence: $"Skipped {r.SkippedReferenceAddresses:N0} references because the tracking limit was reached.",
                Recommendation: "Increase MaxReferenceAddressesToTrack for deeper incoming-reference coverage.",
                Tags: ["analysis-quality", "references"],
                MetricValue: r.SkippedReferenceAddresses,
                MetricUnit: "references"));
        }

        return findings;
    }
}
