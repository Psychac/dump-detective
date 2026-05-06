namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>AllocationPatternAnalyzer</c>.
/// </summary>
public sealed class AllocationPatternAnalysisOptions
{
    /// <summary>
    /// Maximum number of short-lived and long-lived type entries to include.
    /// </summary>
    public int TopTypeLimit { get; init; } = 20;

    /// <summary>
    /// LOH threshold used by downstream consumers expecting a configurable boundary.
    /// </summary>
    public ulong LohThresholdBytes { get; init; } = 85_000;

    /// <summary>
    /// Multiplier used to compute how many top entries are scanned when selecting candidate types.
    /// Effective scan limit = Min(sorted.Count, TopTypeLimit * ScanMultiplier).
    /// </summary>
    public int ScanMultiplier { get; init; } = 2;

    /// <summary>
    /// Selection threshold used to classify a type as a long-lived candidate when
    /// (Gen2 + LOH) / Count &gt; LongLivedSelectionThreshold.
    /// Range: 0.0 - 1.0. Default: 0.3
    /// </summary>
    public double LongLivedSelectionThreshold { get; init; } = 0.3;

    /// <summary>
    /// Classification threshold used when assigning `AllocationProfile.Retained`.
    /// The analyzer previously used 0.5 as the cutoff; exposing this keeps behavior tunable.
    /// </summary>
    public double LongLivedClassificationThreshold { get; init; } = 0.5;

    /// <summary>
    /// Gen0 percentage threshold (0-100) above which a type is considered strictly transient.
    /// Previous hard-coded value: 70.0
    /// </summary>
    public double TransientClassificationThreshold { get; init; } = 70.0;

    /// <summary>
    /// Gen0 percentage threshold (0-100) used to include "short-ish" types in the secondary
    /// short-lived table. Types with Gen0% >= this value (and not long-lived) are eligible.
    /// </summary>
    public double ShortLivedSelectionThreshold { get; init; } = 25.0;

    public static AllocationPatternAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 10,
            ScanMultiplier = 2,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
        AnalysisProfile.Full => new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 50,
            ScanMultiplier = 2,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
        _ => new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 20,
            ScanMultiplier = 2,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
    };

    public static AllocationPatternAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
