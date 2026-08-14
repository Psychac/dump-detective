namespace DumpDetective.Core.Options;

public sealed class AsyncTaskAnalysisOptions
{
    public int MaxTasksToScan { get; init; } = 50_000;
    public int MaxContinuationDepth { get; init; } = 20;
    public int TopTypesToShow { get; init; } = 10;
    public int TopOrphanedToShow { get; init; } = 20;
    public int MaxTcsToScan { get; init; } = 20_000;
    public int TopUnresolvedTcsToShow { get; init; } = 20;

    public static AsyncTaskAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new AsyncTaskAnalysisOptions { MaxTasksToScan = 20_000, MaxContinuationDepth = 10, TopTypesToShow = 8, TopOrphanedToShow = 10, MaxTcsToScan = 10_000, TopUnresolvedTcsToShow = 10 },
        AnalysisProfile.Full => new AsyncTaskAnalysisOptions { MaxTasksToScan = 100_000, MaxContinuationDepth = 40, TopTypesToShow = 20, TopOrphanedToShow = 40, MaxTcsToScan = 40_000, TopUnresolvedTcsToShow = 40 },
        _ => new AsyncTaskAnalysisOptions(),
    };

    public static AsyncTaskAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
