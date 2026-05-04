namespace DumpDetective.Core.Options;

public sealed class ThreadAnalysisOptions
{
    public int MaxFramesForThreadScan { get; init; } = 8;
    public int MaxStackRootsToCount { get; init; } = 256;
}
