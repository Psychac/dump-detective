namespace DumpDetective.Core.Options;

public enum AsyncChainDetectionMode
{
    Disabled,
    CountOnly,
    Full
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
    // When true, prewarm stack-root counts in a background task instead of blocking
    // the main analysis thread. Default false for conservative behavior.
    public bool PrewarmCacheInBackground { get; init; } = false;
    // Adaptive knobs (scale only by dump size tier)
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
            AsyncChainDetection = AsyncChainDetectionMode.Full,
            DetectWaitPatterns = true,
            MaxTopHotspots = 50,
            MaxSampledStackSnapshots = 200,
            SamplingSeed = 0,
            PrewarmCacheInBackground = true
        },
        _ => new ThreadAnalysisOptions(),
    };

    public static ThreadAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);

    // Adapt options heuristically based on dump size tier. This keeps presets
    // behavior stable while reducing work on very large dumps.
    public static ThreadAnalysisOptions AdaptForSize(ThreadAnalysisOptions options, DumpDetective.Core.Models.DumpSizeTier sizeTier)
    {
        int divisor = sizeTier switch
        {
            DumpDetective.Core.Models.DumpSizeTier.Large => 4,
            DumpDetective.Core.Models.DumpSizeTier.Medium => 2,
            _ => 1
        };

        double scaledThreads = Math.Max(0, options.MaxThreadsToCaptureSnapshots / (double)divisor);
        double scaledSampled = Math.Max(0, options.MaxSampledStackSnapshots / (double)divisor);

        int finalThreads = (int)Math.Round(scaledThreads);
        int finalSampled = (int)Math.Round(scaledSampled);

        return new ThreadAnalysisOptions
        {
            MaxFramesForThreadScan = options.MaxFramesForThreadScan,
            MaxStackRootsToCount = options.MaxStackRootsToCount,
            MaxThreadsToCaptureSnapshots = Math.Max(0, finalThreads),
            IncludeStackSamples = options.IncludeStackSamples,
            MaxSampledStackSnapshots = Math.Max(0, finalSampled),
            SamplingSeed = options.SamplingSeed,
            PrewarmCacheInBackground = options.PrewarmCacheInBackground,
            AsyncChainDetection = options.AsyncChainDetection,
            DetectWaitPatterns = options.DetectWaitPatterns,
            MaxTopHotspots = options.MaxTopHotspots
        };
    }
}
