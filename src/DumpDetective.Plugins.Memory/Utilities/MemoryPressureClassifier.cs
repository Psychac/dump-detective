using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Sole owner of the Memory analyzer's pressure-score judgment. Stage 1 (<c>MemoryAnalyzer</c>)
/// emits only raw percentages/ratios on <see cref="MemoryDomainResult"/> (LOH share, top-5
/// concentration, small-object share, object density); every consumer (finding generator, section
/// builder) calls here instead of re-deriving its own weighted formula. See
/// docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md, Smell A.
/// </summary>
internal static class MemoryPressureClassifier
{
    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    public static double ComputeLohPressureScore(MemoryDomainResult r) =>
        Math.Round(Clamp01(r.LohPercent / 35.0) * 100.0, 1);

    public static double ComputeConcentrationPressureScore(MemoryDomainResult r) =>
        Math.Round(Clamp01(r.Top5BytesPercent / 70.0) * 100.0, 1);

    public static double ComputeSmallObjectPressureScore(MemoryDomainResult r) =>
        Math.Round(Clamp01((0.7 * (r.SmallObjectCountPercent / 85.0)) + (0.3 * (r.SmallObjectBytesPercent / 45.0))) * 100.0, 1);

    public static double ComputeDensityPressureScore(MemoryDomainResult r) =>
        Math.Round(Clamp01(r.ObjectsPerMb / 12_000.0) * 100.0, 1);

    public static double ComputeMemoryPressureScore(MemoryDomainResult r) =>
        Math.Round(
            (Clamp01(r.LohPercent / 35.0) * 0.35
           + Clamp01(r.Top5BytesPercent / 70.0) * 0.30
           + Clamp01((0.7 * (r.SmallObjectCountPercent / 85.0)) + (0.3 * (r.SmallObjectBytesPercent / 45.0))) * 0.20
           + Clamp01(r.ObjectsPerMb / 12_000.0) * 0.15) * 100.0,
            1);

    public static string ClassifyPressureBand(double memoryPressureScore) =>
        memoryPressureScore >= 75 ? "High"
        : memoryPressureScore >= 50 ? "Medium"
        : "Low";
}
