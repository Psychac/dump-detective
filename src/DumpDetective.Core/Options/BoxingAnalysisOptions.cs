namespace DumpDetective.Core.Options;

public sealed class BoxingAnalysisOptions
{
    // Arbitrary — "oversized" scales continuously, no external standard to anchor to.
    public int OversizedThresholdBytes { get; init; } = 64;
}
