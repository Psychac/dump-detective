namespace DumpDetective.Core.Options;

public sealed class BoxingAnalysisOptions
{
    public int TypeScanCap { get; init; } = 10_000;
    public int TopBoxedTypeLimit { get; init; } = 20;
    public int TopPaddingLimit { get; init; } = 20;
    public int OversizedThresholdBytes { get; init; } = 64;

    public static BoxingAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new BoxingAnalysisOptions { TypeScanCap = 5_000, TopBoxedTypeLimit = 10, TopPaddingLimit = 10, OversizedThresholdBytes = 96 },
        AnalysisProfile.Full => new BoxingAnalysisOptions { TypeScanCap = 50_000, TopBoxedTypeLimit = 50, TopPaddingLimit = 50, OversizedThresholdBytes = 48 },
        _ => new BoxingAnalysisOptions(),
    };

    public static BoxingAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
