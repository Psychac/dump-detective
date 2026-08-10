namespace DumpDetective.Core.Options;

/// <summary>
/// Configuration options for GC generation analysis.
///
/// Tuning parameters:
/// - <see cref="TopLohTypeLimit"/>: Limits LOH type details shown in reports (default 15)
/// - <see cref="TopGenProfileLimit"/>: Limits per-generation type profiles (default 20)
/// - <see cref="LohThresholdPercent"/>: Only emit LOH Info finding when LOH share exceeds this % (default 20%)
/// - <see cref="Gen0PressureThresholdPercent"/>: Emit Gen0 allocation pressure warning when Gen0 % exceeds this (default 40%)
///
/// Design decisions:
/// - <strong>LohThresholdBytes (removed):</strong> Was defined but never applied in GCGenerationAnalyzer.
///   It created a correctness trap where users could configure a setting with no effect.
///   The <see cref="TopLohTypeLimit"/> already provides the essential LOH filtering (top N by size).
///   Adding a secondary byte-size threshold would create confusing orthogonal controls.
///   Recommendation: Use <see cref="TopLohTypeLimit"/> to control LOH type list size.
/// </summary>
public sealed class GCGenerationAnalysisOptions
{
    /// <summary>Maximum number of Large Object Heap (LOH) types to include in reports.</summary>
    public int TopLohTypeLimit { get; init; } = 15;

    /// <summary>Maximum number of per-type generation profiles to include in detailed analysis.</summary>
    public int TopGenProfileLimit { get; init; } = 20;

    /// <summary>
    /// LOH memory share threshold (%). Only emit LOH Info finding when LOH share exceeds this percentage.
    /// Suppresses noise for healthy dumps where LOH is within expected range. Default 20%.
    /// </summary>
    public double LohThresholdPercent { get; init; } = 20.0;

    /// <summary>
    /// Gen0 allocation pressure threshold (%). Emit Warning when Gen0 objects exceed this % of total objects.
    /// Signals high allocation rate that may degrade GC throughput. Default 40%.
    /// </summary>
    public double Gen0PressureThresholdPercent { get; init; } = 40.0;

    /// <summary>
    /// POH (Pinned Object Heap) memory share threshold (%). Only emit POH Info finding when POH share exceeds this percentage.
    /// POH is a separate heap for pinned objects (.NET 5+). Default 5%.
    /// </summary>
    public double PohThresholdPercent { get; init; } = 5.0;

    public static GCGenerationAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new GCGenerationAnalysisOptions { TopLohTypeLimit = 8, TopGenProfileLimit = 10 },
        AnalysisProfile.Full => new GCGenerationAnalysisOptions { TopLohTypeLimit = 30, TopGenProfileLimit = 40 },
        _ => new GCGenerationAnalysisOptions(),
    };

    public static GCGenerationAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
