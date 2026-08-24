namespace DumpDetective.Core.Options;

public sealed class ArrayAnalysisOptions
{
    // Minimum array length to probe for sparseness. Smaller arrays contribute negligible
    // wasted-byte totals even when fully null/default, so probing them isn't worth the walk.
    public int SparseSampleMinLength { get; init; } = 10_000;

    /// <summary>Maximum number of pinned array instances kept for the "top pinned arrays" table.</summary>
    public int TopPinnedArrayLimit { get; init; } = 50;
}
