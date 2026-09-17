using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class AllocationPatternFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Allocation Pattern Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is AllocationPatternDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not AllocationPatternDomainResult r) return [];

        GCPressureLevel gcPressure = AllocationPatternPressureClassifier.ClassifyPressure(r);
        double promotionPressureScore = AllocationPatternPressureClassifier.ComputePromotionPressureScore(r);

        // Suppress findings at Low pressure (reduces noise)
        if (gcPressure == GCPressureLevel.Low) return [];

        FindingSeverity severity = gcPressure switch
        {
            GCPressureLevel.Critical => FindingSeverity.Critical,
            GCPressureLevel.High => FindingSeverity.Warning,
            _ => FindingSeverity.Info
        };

        // Embed top long-lived type name in evidence for actionability
        string topLongLivedType = r.TopLongLivedTypes is { Count: > 0 }
            ? $" Top long-lived type: {r.TopLongLivedTypes[0].TypeName}."
            : "";

        // Makes the "investigate finalizable types" recommendation below actionable with a count.
        string finalizableNote = r.FinalizableTypeCount > 0
            ? $" {r.FinalizableTypeCount:N0} finalizable type(s) hold {FormatHelper.FormatBytes(r.FinalizableBytes)}."
            : "";

        string evidence = $"Allocation profile: {r.Profile}. " +
            $"Gen0: {r.Gen0CountPct:F1}% obj / {r.Gen0SizePct:F1}% bytes, " +
            $"Gen2: {r.Gen2CountPct:F1}% obj / {r.Gen2SizePct:F1}% bytes, " +
            $"LOH: {r.LohSizePct:F1}% bytes. " +
            $"GC pressure: {gcPressure} (score: {promotionPressureScore:F1}).{topLongLivedType}{finalizableNote}";

        string recommendation = gcPressure switch
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
                Title: $"GC allocation pattern: {r.Profile} ({gcPressure} pressure)",
                Evidence: evidence,
                Recommendation: recommendation,
                Tags: ["gc", "allocation", "gen2", "pressure"],
                MetricValue: promotionPressureScore,
                MetricUnit: "pressure-score")
        ];
    }
}
