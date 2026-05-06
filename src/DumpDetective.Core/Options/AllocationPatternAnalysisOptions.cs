namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>AllocationPatternAnalyzer</c>.
/// </summary>
public sealed class AllocationPatternAnalysisOptions
{
    /// <summary>
    /// How candidate types are ordered when selecting top entries.
    /// - <see cref="TopByCount"/>: order by total object count (original behaviour).
    /// - <see cref="TopByGen0Pct"/>: prefer types with high Gen0 percentage.
    /// - <see cref="TopBySize"/>: prefer types with largest total size.
    /// - <see cref="CompositeScore"/>: weighted composite of Gen0%, Gen2 ratio and LOH size pct.
    /// </summary>
    public enum SelectionMode
    {
        TopByCount,
        TopByGen0Pct,
        TopBySize,
        CompositeScore
    }

    /// <summary>
    /// Strategy used to determine which portion of the candidate set is scanned.
    /// - <see cref="TopN"/>: scan the top-N by count (current default behaviour).
    /// - <see cref="TopNByComparator"/>: sort by the selected comparator, then take top-N.
    /// - <see cref="FullScan"/>: scan all candidates (subject to <see cref="MaxScanItemsAbsolute"/>).
    /// </summary>
    public enum ScanStrategy
    {
        TopN,
        TopNByComparator,
        FullScan
    }

    /// <summary>
    /// Controls the selection order and whether classification is done in a single pass
    /// (collect buckets first) or incrementally (current behaviour).
    /// - <see cref="LongLivedFirst"/>: preserve the previous sequential selection order.
    /// - <see cref="ClassificationFirst"/>: classify the entire scan window into buckets, then pick top-N from each bucket.
    /// - <see cref="Mixed"/>: hybrid behaviour (reserved for future tweaks).
    /// </summary>
    public enum SelectionPriority
    {
        LongLivedFirst,
        ClassificationFirst,
        Mixed
    }

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
    /// Mode used to choose how candidate entries are ordered for selection.
    /// Default preserves current behaviour (TopByCount).
    /// </summary>
    public SelectionMode Mode { get; init; } = SelectionMode.TopByCount;

    /// <summary>
    /// Strategy used to determine how many items to scan.
    /// Default preserves current behaviour (`TopN`).
    /// </summary>
    public ScanStrategy Strategy { get; init; } = ScanStrategy.TopN;

    /// <summary>
    /// Safety cap applied when `Strategy == FullScan` to avoid unbounded work on huge type sets.
    /// </summary>
    public int MaxScanItemsAbsolute { get; init; } = 10000;

    /// <summary>
    /// Strategy used to select and prioritize classification vs selection. Defaults to <see cref="SelectionPriority.LongLivedFirst"/>.
    /// </summary>
    public SelectionPriority Priority { get; init; } = SelectionPriority.LongLivedFirst;

    /// <summary>
    /// Emit flags allow presets to disable emission of specific result tables. When false the analyzer will return an empty list
    /// for the corresponding category and the section builder will not render the table.
    /// Defaults: all true (emit everything).
    /// </summary>
    public bool EmitTransient { get; init; } = true;
    public bool EmitShortish { get; init; } = true;
    public bool EmitLongLived { get; init; } = true;

    /// <summary>
    /// Weights used when computing the composite score (SelectionMode.CompositeScore).
    /// CompositeScore = Gen0Pct*Gen0Weight + (Gen2Ratio*100.0)*Gen2Weight + LohSizePct*LohSizeWeight
    /// </summary>
    public double Gen0Weight { get; init; } = 1.0;
    public double Gen2Weight { get; init; } = 1.0;
    public double LohSizeWeight { get; init; } = 1.0;

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
            ScanMultiplier = 1,
            Mode = SelectionMode.TopByCount,
            Strategy = ScanStrategy.TopN,
            Priority = SelectionPriority.LongLivedFirst,
            EmitShortish = false,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
        AnalysisProfile.Full => new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 50,
            ScanMultiplier = 2,
            Mode = SelectionMode.CompositeScore,
            Strategy = ScanStrategy.FullScan,
            MaxScanItemsAbsolute = 20000,
            Priority = SelectionPriority.ClassificationFirst,
            Gen0Weight = 0.5,
            Gen2Weight = 1.5,
            LohSizeWeight = 1.0,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
        _ => new AllocationPatternAnalysisOptions
        {
            TopTypeLimit = 20,
            ScanMultiplier = 2,
            Mode = SelectionMode.CompositeScore,
            Strategy = ScanStrategy.TopNByComparator,
            Priority = SelectionPriority.ClassificationFirst,
            Gen0Weight = 1.0,
            Gen2Weight = 1.0,
            LohSizeWeight = 1.0,
            LongLivedSelectionThreshold = 0.3,
            LongLivedClassificationThreshold = 0.5,
            TransientClassificationThreshold = 70.0,
            ShortLivedSelectionThreshold = 25.0,
        },
    };

    public static AllocationPatternAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
