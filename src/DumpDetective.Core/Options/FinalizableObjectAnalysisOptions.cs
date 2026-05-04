namespace DumpDetective.Core.Options;

public sealed class FinalizableObjectAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int QueueScanLimit { get; init; } = 500;
    public int TopQueueEntries { get; init; } = 10;
    public int MaxBfsNodes { get; init; } = 200;
    public int MaxBfsDepth { get; init; } = 10;

    public static FinalizableObjectAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new FinalizableObjectAnalysisOptions { TopTypeLimit = 10, QueueScanLimit = 200, TopQueueEntries = 5, MaxBfsNodes = 100, MaxBfsDepth = 8 },
        AnalysisProfile.Full => new FinalizableObjectAnalysisOptions { TopTypeLimit = 50, QueueScanLimit = 2_000, TopQueueEntries = 25, MaxBfsNodes = 1_000, MaxBfsDepth = 20 },
        _ => new FinalizableObjectAnalysisOptions(),
    };

    public static FinalizableObjectAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
