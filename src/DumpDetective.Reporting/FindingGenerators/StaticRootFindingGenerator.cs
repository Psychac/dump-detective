using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class StaticRootFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Static Root Leak Detection";
    public bool CanGenerate(AnalyzerDomainResult result) => result is StaticRootDomainResult;

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

        FindingSeverity severity = r.RootCount >= 10 ? FindingSeverity.Critical : FindingSeverity.Warning;

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
                MetricUnit: "retained-bytes")
        ];
    }
}
