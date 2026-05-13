using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class DominatorFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Dominator Analysis";

    public bool CanGenerate(AnalyzerDomainResult result) => result is DominatorDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not DominatorDomainResult r || r.TopDominatorTypes.Count == 0)
            return [];

        TypeSnapshot top = r.TopDominatorTypes[0];
        FindingSeverity severity = top.EstimatedRetainedBytes >= 500UL * 1024 * 1024
            ? FindingSeverity.Critical
            : top.EstimatedRetainedBytes >= 100UL * 1024 * 1024
                ? FindingSeverity.Warning
                : FindingSeverity.Info;

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Memory",
                Severity: severity,
                Title: $"Dominant retention suspect: {top.TypeName}",
                Evidence: $"{top.TypeName} estimates {FormatHelper.FormatBytes(top.EstimatedRetainedBytes)} retained across {top.Count:N0} instances.",
                Recommendation: "Use the retention section and root paths to confirm why this type remains live.",
                Tags: ["dominator", "retained-bytes", top.TypeName],
                MetricValue: top.EstimatedRetainedBytes,
                MetricUnit: "bytes")
        ];
    }
}