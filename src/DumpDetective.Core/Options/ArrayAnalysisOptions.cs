namespace DumpDetective.Core.Options;

public sealed class ArrayAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int TopLargeLimit { get; init; } = 20;
    public int TopSparseLimit { get; init; } = 10;
    public int SparseSampleLimit { get; init; } = 500;
    public int SparseSampleMinLength { get; init; } = 10_000;
    public int SampleStride { get; init; } = 100;
}
