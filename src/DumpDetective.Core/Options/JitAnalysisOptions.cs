namespace DumpDetective.Core.Options;

public sealed class JitAnalysisOptions
{
    public int MaxFramesPerThread { get; init; } = 200;
    public int TopMethodsLimit { get; init; } = 20;
    public int TopFrameTypesLimit { get; init; } = 20;
    public uint LargeMethodThresholdBytes { get; init; } = 64 * 1024;

    public static JitAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new JitAnalysisOptions { MaxFramesPerThread = 100, TopMethodsLimit = 10, TopFrameTypesLimit = 10, LargeMethodThresholdBytes = 96 * 1024 },
        AnalysisProfile.Full => new JitAnalysisOptions { MaxFramesPerThread = 400, TopMethodsLimit = 50, TopFrameTypesLimit = 50, LargeMethodThresholdBytes = 32 * 1024 },
        _ => new JitAnalysisOptions(),
    };

    public static JitAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
