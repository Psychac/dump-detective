namespace DumpDetective.Core.Options;

public sealed class JitAnalysisOptions
{
    public int MaxFramesPerThread { get; init; } = 200;
    public int TopMethodsLimit { get; init; } = 20;
    public int TopFrameTypesLimit { get; init; } = 20;
    public uint LargeMethodThresholdBytes { get; init; } = 64 * 1024;
}
