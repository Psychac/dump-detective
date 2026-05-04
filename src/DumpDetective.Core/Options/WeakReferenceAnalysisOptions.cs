namespace DumpDetective.Core.Options;

public sealed class WeakReferenceAnalysisOptions
{
    public int HandleScanCap { get; init; } = 50_000;
    public int TopTypeLimit { get; init; } = 15;

    public static WeakReferenceAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new WeakReferenceAnalysisOptions { HandleScanCap = 20_000, TopTypeLimit = 8 },
        AnalysisProfile.Full => new WeakReferenceAnalysisOptions { HandleScanCap = 200_000, TopTypeLimit = 40 },
        _ => new WeakReferenceAnalysisOptions(),
    };

    public static WeakReferenceAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
