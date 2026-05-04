namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>ThreadStackClusterAnalyzer</c>.
/// </summary>
public sealed class ThreadStackClusterAnalysisOptions
{
    public int MaxFramesPerSignature { get; init; } = 6;
    public int MaxThreadIdsPerCluster { get; init; } = 8;
    public int TopSignaturesToShow { get; init; } = 5;
    public int TopClustersToShow { get; init; } = 12;
}
