namespace DumpDetective.Core.Options;

public sealed class GCRootAnalysisOptions
{
    public int TopSeverityLimit { get; init; } = 20;
    public int PathSearchTopN { get; init; } = 25;
    public int MaxBfsNodes { get; init; } = 500;
    public int MaxBfsDepth { get; init; } = 20;
}
