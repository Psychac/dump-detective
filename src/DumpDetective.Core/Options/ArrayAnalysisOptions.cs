namespace DumpDetective.Core.Options;

public sealed class ArrayAnalysisOptions
{
    public int TopTypeLimit { get; init; } = 20;
    public int TopLargeLimit { get; init; } = 20;
    public int TopSparseLimit { get; init; } = 10;
    public int SparseSampleLimit { get; init; } = 500;
    public int SparseSampleMinLength { get; init; } = 10_000;
    public int SampleStride { get; init; } = 100;

    public static ArrayAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new ArrayAnalysisOptions { TopTypeLimit = 10, TopLargeLimit = 10, TopSparseLimit = 5, SparseSampleLimit = 200, SparseSampleMinLength = 20_000, SampleStride = 200 },
        AnalysisProfile.Full => new ArrayAnalysisOptions { TopTypeLimit = 50, TopLargeLimit = 50, TopSparseLimit = 20, SparseSampleLimit = 1000, SparseSampleMinLength = 5_000, SampleStride = 50 },
        _ => new ArrayAnalysisOptions(),
    };

    public static ArrayAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
