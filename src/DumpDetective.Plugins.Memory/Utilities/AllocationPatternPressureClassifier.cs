using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Sole owner of the Allocation Pattern analyzer's GC-pressure judgment. Stage 1
/// (<c>AllocationPatternAnalyzer</c>) emits only raw generation percentages on
/// <see cref="AllocationPatternDomainResult"/> (<c>Gen0CountPct</c>, <c>Gen2CountPct</c>,
/// <c>LohSizePct</c>); every consumer (finding generator, section builder, trend comparer,
/// insight engine, executive summary) calls here instead of re-deriving its own weighted formula.
/// See docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md, Smell A.
/// </summary>
internal static class AllocationPatternPressureClassifier
{
    public static double ComputePressureScore(AllocationPatternDomainResult r) =>
        ((100.0 - r.Gen0CountPct) * 0.3) + (r.Gen2CountPct * 0.5) + (r.LohSizePct * 0.2);

    public static GCPressureLevel ClassifyPressure(AllocationPatternDomainResult r)
    {
        double score = ComputePressureScore(r);
        if (score > 70.0) return GCPressureLevel.Critical;
        if (score > 45.0) return GCPressureLevel.High;
        if (score > 20.0) return GCPressureLevel.Moderate;
        return GCPressureLevel.Low;
    }

    public static double ComputePromotionPressureScore(AllocationPatternDomainResult r) =>
        r.Gen2CountPct + (r.LohSizePct * 2.0);
}
