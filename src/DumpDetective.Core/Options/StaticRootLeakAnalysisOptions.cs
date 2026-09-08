namespace DumpDetective.Core.Options;

public sealed class StaticRootLeakAnalysisOptions
{
    public ulong SignificantMemoryThresholdBytes { get; init; } = 1024 * 1024;
    public int SignificantObjectCountThreshold { get; init; } = 100;
}
