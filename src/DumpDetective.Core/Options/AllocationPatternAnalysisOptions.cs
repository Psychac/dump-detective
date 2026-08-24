namespace DumpDetective.Core.Options;

/// <summary>
/// Semantic thresholds for <c>AllocationPatternAnalyzer</c>'s classification. All Category-1/2/3/4
/// knobs (selection mode/strategy/priority enums, scan caps, per-table row limits, emit-table
/// toggles) were deleted per docs/refactor/analysis-profile-removal-plan.md §9.30/D7 — exactness
/// means always classifying the complete type population with the one correct algorithm
/// (CompositeScore ranking, classify-first bucketing) and emitting the complete ranked result.
/// </summary>
public sealed class AllocationPatternAnalysisOptions
{
    /// <summary>
    /// Weights for CompositeScore = Gen0Pct*Gen0Weight + (Gen2Ratio*100)*Gen2Weight + LohSizePct*LohSizeWeight.
    /// Equal weighting is the least arbitrary default; shaky — revisit once field data exists on
    /// which signal should dominate (D4/D7, §11.2).
    /// </summary>
    public double Gen0Weight { get; init; } = 1.0;
    public double Gen2Weight { get; init; } = 1.0;
    public double LohSizeWeight { get; init; } = 1.0;

    /// <summary>
    /// A type is a long-lived selection candidate when (Gen2 + LOH) / Count exceeds this ratio.
    /// Range: 0.0-1.0.
    /// </summary>
    public double LongLivedSelectionThreshold { get; init; } = 0.3;

    /// <summary>
    /// Classification cutoff for <see cref="TypeProfile.Retained"/> vs <see cref="TypeProfile.Mixed"/>.
    /// </summary>
    public double LongLivedClassificationThreshold { get; init; } = 0.5;

    /// <summary>
    /// Gen0 percentage (0-100) above which a type is considered strictly transient.
    /// </summary>
    public double TransientClassificationThreshold { get; init; } = 70.0;

    /// <summary>
    /// Gen0 percentage (0-100) used to include "short-ish" types in the secondary short-lived
    /// table. Types with Gen0% >= this value (and not long-lived) are eligible.
    /// </summary>
    public double ShortLivedSelectionThreshold { get; init; } = 25.0;
}
