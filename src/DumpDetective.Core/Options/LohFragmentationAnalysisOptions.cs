namespace DumpDetective.Core.Options;

public sealed class LohFragmentationAnalysisOptions
{
    public int TopSegments { get; init; } = 10;
    public int TopLargeObjectsCount { get; init; } = 20;

    public static LohFragmentationAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new LohFragmentationAnalysisOptions { TopSegments = 5, TopLargeObjectsCount = 10 },
        AnalysisProfile.Full => new LohFragmentationAnalysisOptions { TopSegments = 25, TopLargeObjectsCount = 60 },
        _ => new LohFragmentationAnalysisOptions(),
    };

    public static LohFragmentationAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
