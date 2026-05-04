namespace DumpDetective.Core.Options;

public sealed class GCHandleAnalysisOptions
{
    public int TopTypeCount { get; init; } = 15;

    public static GCHandleAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new GCHandleAnalysisOptions { TopTypeCount = 8 },
        AnalysisProfile.Full => new GCHandleAnalysisOptions { TopTypeCount = 40 },
        _ => new GCHandleAnalysisOptions(),
    };

    public static GCHandleAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
