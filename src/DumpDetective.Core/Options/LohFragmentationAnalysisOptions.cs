namespace DumpDetective.Core.Options;

public sealed class LohFragmentationAnalysisOptions
{
    public int TopSegments { get; init; } = 10;
    public int TopLargeObjectsCount { get; init; } = 20;
}
