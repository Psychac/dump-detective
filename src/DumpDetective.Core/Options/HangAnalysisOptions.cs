namespace DumpDetective.Core.Options;

public sealed class HangAnalysisOptions
{
    public int LongWaitThreshold { get; init; } = 5;
    public int HighThreadPoolThreshold { get; init; } = 100;
    public int MaxTasksToScan { get; init; } = 50_000;
    public int TopWaitingThreadsPerGroup { get; init; } = 5;
    public int TopContinuationTypesToShow { get; init; } = 5;
}
