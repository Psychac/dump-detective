namespace DumpDetective.Core.Options;

public sealed class StaticRootLeakAnalysisOptions
{
    public int MaxRootsToReport { get; init; } = 15;
    public int TopRetainedTypesToReport { get; init; } = 5;
    public int SampleRetainedObjectsToInspect { get; init; } = 100;
    public ulong SignificantMemoryThresholdBytes { get; init; } = 1024 * 1024;
    public int SignificantObjectCountThreshold { get; init; } = 100;
    public int MaxRetainedObjectsToScan { get; init; } = 10000;

    public static StaticRootLeakAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new StaticRootLeakAnalysisOptions { MaxRootsToReport = 8, TopRetainedTypesToReport = 3, SampleRetainedObjectsToInspect = 50, SignificantMemoryThresholdBytes = 2 * 1024 * 1024, SignificantObjectCountThreshold = 200, MaxRetainedObjectsToScan = 5_000 },
        AnalysisProfile.Full => new StaticRootLeakAnalysisOptions { MaxRootsToReport = 40, TopRetainedTypesToReport = 15, SampleRetainedObjectsToInspect = 500, SignificantMemoryThresholdBytes = 512 * 1024, SignificantObjectCountThreshold = 50, MaxRetainedObjectsToScan = 50_000 },
        _ => new StaticRootLeakAnalysisOptions(),
    };

    public static StaticRootLeakAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
