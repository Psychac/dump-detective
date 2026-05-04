namespace DumpDetective.Core.Options;

public sealed class GCGenerationAnalysisOptions
{
    public ulong LohThresholdBytes { get; init; } = 85_000;
    public int TopLohTypeLimit { get; init; } = 15;
    public int TopGenProfileLimit { get; init; } = 20;
}
