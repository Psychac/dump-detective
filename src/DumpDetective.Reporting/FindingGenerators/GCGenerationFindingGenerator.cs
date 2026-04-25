using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class GCGenerationFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "GC Generation Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is GCGenerationDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not GCGenerationDomainResult r) return [];
        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: r.LohPercent >= 35 ? FindingSeverity.Warning : FindingSeverity.Info,
                Title: "GC generation footprint snapshot",
                Evidence: $"LOH memory share is {r.LohPercent:F1}% of managed heap.",
                Recommendation: r.LohPercent >= 35
                    ? "Inspect large object churn and promotion patterns."
                    : "Generation split appears within expected range for this dump.",
                Tags: ["gc", "generations", "loh"],
                MetricValue: r.LohPercent,
                MetricUnit: "%")
        ];
    }
}
