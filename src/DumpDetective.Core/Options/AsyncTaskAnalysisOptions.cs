namespace DumpDetective.Core.Options;

public sealed class AsyncTaskAnalysisOptions
{
    public int MaxTasksToScan { get; init; } = 50_000;
    public int MaxContinuationDepth { get; init; } = 20;
    public int TopTypesToShow { get; init; } = 10;
    public int TopOrphanedToShow { get; init; } = 20;
}
