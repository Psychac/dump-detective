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
}
