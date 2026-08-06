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
    public bool ProduceClusterExports { get; init; } = false;
    public int MinClusterSize { get; init; } = 1;
    public int MaxClusters { get; init; } = 500;

    public static ThreadStackClusterAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ThreadStackClusterAnalysisOptions
        {
            MaxFramesPerSignature = 4,
            MaxThreadIdsPerCluster = 5,
            TopSignaturesToShow = 3,
            TopClustersToShow = 8,
            MinClusterSize = 1,
            MaxClusters = 200,
            ProduceClusterExports = false
        },
        AnalysisProfile.Full => new ThreadStackClusterAnalysisOptions
        {
            MaxFramesPerSignature = 10,
            MaxThreadIdsPerCluster = 20,
            TopSignaturesToShow = 10,
            TopClustersToShow = 20,
            MinClusterSize = 1,
            MaxClusters = 2000,
            ProduceClusterExports = true
        },
        _ => new ThreadStackClusterAnalysisOptions(),
    };

    public static ThreadStackClusterAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
