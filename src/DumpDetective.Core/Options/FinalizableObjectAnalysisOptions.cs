namespace DumpDetective.Core.Options;

public sealed class FinalizableObjectAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int QueueScanLimit { get; init; } = 500;
    public int TopQueueEntries { get; init; } = 10;
    public int MaxBfsNodes { get; init; } = 200;
    public int MaxBfsDepth { get; init; } = 10;
}
