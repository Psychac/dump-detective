namespace DumpDetective.Core.Options;

public sealed class StaticRootLeakAnalysisOptions
{
    public int MaxRootsToReport { get; init; } = 15;
    public int TopRetainedTypesToReport { get; init; } = 5;
    public int SampleRetainedObjectsToInspect { get; init; } = 100;
    public ulong SignificantMemoryThresholdBytes { get; init; } = 1024 * 1024;
    public int SignificantObjectCountThreshold { get; init; } = 100;
    public int MaxRetainedObjectsToScan { get; init; } = 10000;
}
