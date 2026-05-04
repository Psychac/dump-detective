namespace DumpDetective.Core.Options;

public sealed class MemoryAnalysisOptions
{
    public ulong LohThresholdBytes { get; init; } = 85_000;
    public int TopBySizeCount { get; init; } = 20;
    public int TopByCountCount { get; init; } = 20;

    public static MemoryAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new MemoryAnalysisOptions { TopBySizeCount = 10, TopByCountCount = 10 },
        AnalysisProfile.Full => new MemoryAnalysisOptions { TopBySizeCount = 50, TopByCountCount = 50 },
        _ => new MemoryAnalysisOptions(),
    };

    public static MemoryAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
