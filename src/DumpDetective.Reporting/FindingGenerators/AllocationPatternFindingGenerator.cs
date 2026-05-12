using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class AllocationPatternFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Allocation Pattern Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AllocationPatternDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AllocationPatternDomainResult r) return [];

        FindingSeverity severity = r.GCPressure switch
        {
            GCPressureLevel.Critical => FindingSeverity.Critical,
            GCPressureLevel.High => FindingSeverity.Warning,
            _ => FindingSeverity.Info
        };

        string evidence = $"Allocation profile: {r.Profile}. " +
            $"Gen0: {r.Gen0CountPct:F1}% obj / {r.Gen0SizePct:F1}% bytes, " +
            $"Gen2: {r.Gen2CountPct:F1}% obj / {r.Gen2SizePct:F1}% bytes, " +
            $"LOH: {r.LohSizePct:F1}% bytes. " +
            $"GC pressure: {r.GCPressure} (score: {r.PromotionPressureScore:F1}).";

        string recommendation = r.GCPressure switch
        {
            GCPressureLevel.Critical =>
                "Critical GC pressure. Large Gen2 and/or LOH accumulation detected. Investigate long-lived objects and finalizable types.",
            GCPressureLevel.High =>
                "High GC pressure. Reduce Gen2 retention — review caches, static fields, and event subscriptions.",
            GCPressureLevel.Moderate =>
                "Moderate GC pressure. Monitor Gen2 growth across snapshots.",
            _ =>
                "GC pressure is within normal bounds."
        };

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "GC",
                Severity: severity,
                Title: $"GC allocation pattern: {r.Profile} ({r.GCPressure} pressure)",
                Evidence: evidence,
                Recommendation: recommendation,
                Tags: ["gc", "allocation", "gen2", "pressure"],
                MetricValue: r.PromotionPressureScore,
                MetricUnit: "pressure-score")
        ];
    }
}
