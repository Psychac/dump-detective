namespace DumpDetective.Core.Options;

public sealed class GCGenerationAnalysisOptions
{
    public int TopLohTypeLimit { get; init; } = 15;
    public int TopGenProfileLimit { get; init; } = 20;
    public double LohThresholdPercent { get; init; } = 20.0;

    public static GCGenerationAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new GCGenerationAnalysisOptions { TopLohTypeLimit = 8, TopGenProfileLimit = 10 },
        AnalysisProfile.Full => new GCGenerationAnalysisOptions { TopLohTypeLimit = 30, TopGenProfileLimit = 40 },
        _ => new GCGenerationAnalysisOptions(),
    };

    public static GCGenerationAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
