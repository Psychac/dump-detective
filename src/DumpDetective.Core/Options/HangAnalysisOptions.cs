namespace DumpDetective.Core.Options;

public sealed class HangAnalysisOptions
{
    public int LongWaitThreshold { get; init; } = 5;
    public int HighThreadPoolThreshold { get; init; } = 100;
    public int MaxTasksToScan { get; init; } = 50_000;
    public int TopWaitingThreadsPerGroup { get; init; } = 5;
    public int TopContinuationTypesToShow { get; init; } = 5;

    public static HangAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new HangAnalysisOptions { LongWaitThreshold = 8, HighThreadPoolThreshold = 150, MaxTasksToScan = 20_000, TopWaitingThreadsPerGroup = 3, TopContinuationTypesToShow = 3 },
        AnalysisProfile.Full => new HangAnalysisOptions { LongWaitThreshold = 3, HighThreadPoolThreshold = 60, MaxTasksToScan = 150_000, TopWaitingThreadsPerGroup = 10, TopContinuationTypesToShow = 15 },
        _ => new HangAnalysisOptions(),
    };

    public static HangAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
