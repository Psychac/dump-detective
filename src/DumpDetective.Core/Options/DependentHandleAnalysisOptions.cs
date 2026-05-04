namespace DumpDetective.Core.Options;

public sealed class DependentHandleAnalysisOptions
{
    public int TopCount { get; init; } = 15;

    public static DependentHandleAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new DependentHandleAnalysisOptions { TopCount = 8 },
        AnalysisProfile.Full => new DependentHandleAnalysisOptions { TopCount = 40 },
        _ => new DependentHandleAnalysisOptions(),
    };

    public static DependentHandleAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
