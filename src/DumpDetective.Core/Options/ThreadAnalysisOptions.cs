namespace DumpDetective.Core.Options;

public enum AsyncChainDetectionMode
{
    Disabled,
    CountOnly,
    Full,
    FullWithPaths
}

public sealed class ThreadAnalysisOptions
{
    public int MaxFramesForThreadScan { get; init; } = 8;
    public int MaxStackRootsToCount { get; init; } = 256;

    // New behavioral knobs
    public int MaxThreadsToCaptureSnapshots { get; init; } = 20;
    public bool IncludeStackSamples { get; init; } = true;
    public int MaxSampledStackSnapshots { get; init; } = 20;
    // Deterministic sampling seed; 0 means "derive from dump identifier"
    public int SamplingSeed { get; init; } = 0;
    public AsyncChainDetectionMode AsyncChainDetection { get; init; } = AsyncChainDetectionMode.Full;
    public bool DetectWaitPatterns { get; init; } = true;
    public int MaxTopHotspots { get; init; } = 10;

    public static ThreadAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ThreadAnalysisOptions
        {
            MaxFramesForThreadScan = 4,
            MaxStackRootsToCount = 128,
            MaxThreadsToCaptureSnapshots = 10,
            IncludeStackSamples = false,
            AsyncChainDetection = AsyncChainDetectionMode.CountOnly,
            DetectWaitPatterns = true,
            MaxTopHotspots = 10,
            MaxSampledStackSnapshots = 0,
            SamplingSeed = 0
        },
        AnalysisProfile.Full => new ThreadAnalysisOptions
        {
            MaxFramesForThreadScan = 16,
            MaxStackRootsToCount = 1_024,
            MaxThreadsToCaptureSnapshots = 50,
            IncludeStackSamples = true,
            AsyncChainDetection = AsyncChainDetectionMode.FullWithPaths,
            DetectWaitPatterns = true,
            MaxTopHotspots = 50,
            MaxSampledStackSnapshots = 200,
            SamplingSeed = 0
        },
        _ => new ThreadAnalysisOptions(),
    };

    public static ThreadAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
