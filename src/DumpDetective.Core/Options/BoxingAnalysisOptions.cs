namespace DumpDetective.Core.Options;

public sealed class BoxingAnalysisOptions
{
    public int TypeScanCap { get; init; } = 10_000;
    public int TopBoxedTypeLimit { get; init; } = 20;
    public int TopPaddingLimit { get; init; } = 20;
    public int OversizedThresholdBytes { get; init; } = 64;
}
