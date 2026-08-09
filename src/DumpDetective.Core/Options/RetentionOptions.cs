namespace DumpDetective.Core.Options;

public sealed class RetentionOptions
{
    public int TopFinalizerTypesToShow { get; init; } = 10;
    public int TopHighlyReferencedObjectsToShow { get; init; } = 15;

    public int HighReferenceThreshold { get; init; } = 50;
    public int MaxReferenceAddresses { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum number of objects subjected to full reference-field enumeration during
    /// the incoming-reference-count pass. Each traced object requires at least one
    /// <c>heap.GetObject()</c> call against the dump file, which is the primary
    /// bottleneck on large (multi-GB) dumps.
    /// Default: 2 000 000. Set to 0 to disable the limit (only safe on small dumps).
    /// When the limit is reached, ObjectScanCapped is set to true in the retention analyzer result.
    /// is set to <c>true</c> and a confidence note is emitted in the report.
    /// </summary>
    public int MaxLeakScanObjects { get; init; } = 2_000_000;

    /// <summary>
    /// Maximum number of candidate nodes explored during root path search.
    /// Limits the breadth of the BFS when searching for GC root paths.
    /// Default: 5 000. Set higher for more thorough evidence collection.
    /// </summary>
    public int MaxRootPathCandidateNodes { get; init; } = 5_000;

    /// <summary>
    /// Maximum depth (hops from root) explored during candidate search phase of root path finding.
    /// Default: 8.
    /// </summary>
    public int MaxRootPathCandidateDepth { get; init; } = 8;

    /// <summary>
    /// Maximum depth explored when expanding from a root node towards the target object.
    /// Default: 12.
    /// </summary>
    public int MaxRootPathExpansionDepth { get; init; } = 12;

    /// <summary>
    /// Fanout threshold above which a reference path is considered "large" and skipped
    /// to avoid exploring extremely high-connectivity clusters.
    /// Default: 100.
    /// </summary>
    public int RootPathLargeFanoutThreshold { get; init; } = 100;

    public static RetentionOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new RetentionOptions
        {
            TopFinalizerTypesToShow = 5,
            TopHighlyReferencedObjectsToShow = 8,
            HighReferenceThreshold = 75,
            MaxReferenceAddresses = 250_000,
            MaxLeakScanObjects = 500_000,
            MaxRootPathCandidateNodes = 2_000,
            MaxRootPathCandidateDepth = 6,
            MaxRootPathExpansionDepth = 8,
            RootPathLargeFanoutThreshold = 50
        },
        AnalysisProfile.Full => new RetentionOptions
        {
            TopFinalizerTypesToShow = 25,
            TopHighlyReferencedObjectsToShow = 40,
            HighReferenceThreshold = 30,
            MaxReferenceAddresses = 2_000_000,
            MaxLeakScanObjects = 5_000_000,
            MaxRootPathCandidateNodes = 10_000,
            MaxRootPathCandidateDepth = 12,
            MaxRootPathExpansionDepth = 16,
            RootPathLargeFanoutThreshold = 200
        },
        _ => new RetentionOptions()
    };

    public static RetentionOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
