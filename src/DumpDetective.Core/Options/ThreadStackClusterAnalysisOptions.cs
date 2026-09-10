namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>ThreadStackClusterAnalyzer</c>.
/// </summary>
public sealed class ThreadStackClusterAnalysisOptions
{
    // Semantic threshold (Category 5): a cluster of size 1 is always a singleton, never
    // "interesting" on its own — kept as the single Balanced value, not tier-varied.
    public int MinClusterSize { get; init; } = 1;
}
