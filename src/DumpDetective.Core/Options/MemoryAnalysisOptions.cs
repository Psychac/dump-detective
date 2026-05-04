namespace DumpDetective.Core.Options;

public sealed class MemoryAnalysisOptions
{
    public ulong LohThresholdBytes { get; init; } = 85_000;
    public int TopBySizeCount { get; init; } = 20;
    public int TopByCountCount { get; init; } = 20;
}
