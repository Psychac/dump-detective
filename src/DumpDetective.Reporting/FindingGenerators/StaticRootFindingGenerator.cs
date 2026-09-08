using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class StaticRootFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Static Root Leak Detection";
    public bool CanGenerate(AnalyzerDomainResult result) => result is StaticRootDomainResult;

    private static FindingSeverity DetermineSeverity(StaticRootDomainResult result, ulong criticalByteThreshold)
    {
        if (result.RootCount >= 10)
            return FindingSeverity.Critical;

        var topRoots = result.TopRootsByRetainedBytes;
        if (topRoots?.Count > 0 && topRoots[0].TotalMemoryImpact > criticalByteThreshold)
            return FindingSeverity.Critical;

        return FindingSeverity.Warning;
    }

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not StaticRootDomainResult r) return [];

        if (r.RootCount == 0)
        {
            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Leak",
                    Severity: FindingSeverity.Info,
                    Title: "No static-root retention candidates detected",
                    Evidence: "No static roots with significant retained object graphs were found.",
                    Recommendation: "No static-root action required for this snapshot.",
                    Tags: ["static-root", "retention"],
                    MetricValue: 0,
                    MetricUnit: "retained-bytes")
            ];
        }

        const ulong CriticalByteThreshold = 100 * 1_024 * 1_024;
        FindingSeverity severity = DetermineSeverity(r, CriticalByteThreshold);
        Evidence? topEvidence = r.TopRootsByRetainedBytes?.Count > 0 ? r.TopRootsByRetainedBytes[0].Evidence : null;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Leak",
                Severity: severity,
                Title: "Static-root retention candidates detected",
                Evidence: $"{r.RootCount:N0} root(s) retain ~{FormatHelper.FormatBytes(r.TotalRetainedBytes)} cumulative memory.",
                Recommendation: "Audit static ownership and clear or weaken references for expired object graphs.",
                Tags: ["static-root", "retention", "memory-leak"],
                MetricValue: r.TotalRetainedBytes,
                MetricUnit: "retained-bytes",
                ConfidenceScore: EvidenceConfidence.Compute(topEvidence))
        ];
    }
}
