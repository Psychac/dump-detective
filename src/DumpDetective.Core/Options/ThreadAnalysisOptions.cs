namespace DumpDetective.Core.Options;

public sealed class ThreadAnalysisOptions
{
    public int MaxFramesForThreadScan { get; init; } = 8;
    public int MaxStackRootsToCount { get; init; } = 256;

    public static ThreadAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ThreadAnalysisOptions { MaxFramesForThreadScan = 4, MaxStackRootsToCount = 128 },
        AnalysisProfile.Full => new ThreadAnalysisOptions { MaxFramesForThreadScan = 16, MaxStackRootsToCount = 1_024 },
        _ => new ThreadAnalysisOptions(),
    };

    public static ThreadAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
