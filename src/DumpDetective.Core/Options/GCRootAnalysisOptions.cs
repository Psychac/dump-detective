namespace DumpDetective.Core.Options;

public sealed class GCRootAnalysisOptions
{
    public int TopSeverityLimit { get; init; } = 20;
    public int PathSearchTopN { get; init; } = 25;
    public int MaxBfsNodes { get; init; } = 500;
    public int MaxBfsDepth { get; init; } = 20;

    public static GCRootAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new GCRootAnalysisOptions { TopSeverityLimit = 10, PathSearchTopN = 10, MaxBfsNodes = 250, MaxBfsDepth = 10 },
        AnalysisProfile.Full => new GCRootAnalysisOptions { TopSeverityLimit = 40, PathSearchTopN = 60, MaxBfsNodes = 2_000, MaxBfsDepth = 30 },
        _ => new GCRootAnalysisOptions(),
    };

    public static GCRootAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
