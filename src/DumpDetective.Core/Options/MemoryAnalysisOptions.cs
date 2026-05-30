namespace DumpDetective.Core.Options;

public sealed class MemoryAnalysisOptions
{
    /// <summary>
    /// Minimum object size (bytes) treated as LOH for memory summary metrics.
    /// </summary>
    public ulong LohThresholdBytes { get; init; } = 85_000;

    /// <summary>
    /// Final cap for the unified "top types" list shown in reports.
    /// </summary>
    public int TopTypesCount { get; init; } = 20;

    /// <summary>
    /// Relative weight for selecting types by total bytes.
    /// </summary>
    public int TopTypesBySizeWeight { get; init; } = 40;

    /// <summary>
    /// Relative weight for selecting types by object count.
    /// Helps surface high-frequency small objects.
    /// </summary>
    public int TopTypesByCountWeight { get; init; } = 35;

    /// <summary>
    /// Relative weight for selecting types by LOH bytes.
    /// Helps surface large-object pressure and fragmentation risk.
    /// </summary>
    public int TopTypesByLohWeight { get; init; } = 15;

    /// <summary>
    /// Relative weight for selecting types by average instance size.
    /// Helps surface very large individual objects.
    /// </summary>
    public int TopTypesByAverageSizeWeight { get; init; } = 10;

    /// <summary>
    /// Profile presets tune top-type breadth vs depth:
    /// Fast favors bytes/count, Full gives more room to LOH/avg-size signals,
    /// and Balanced (default) aims for broad actionable coverage.
    /// </summary>
    public static MemoryAnalysisOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new MemoryAnalysisOptions
        {
            TopTypesCount = 10,
            TopTypesBySizeWeight = 45,
            TopTypesByCountWeight = 40,
            TopTypesByLohWeight = 10,
            TopTypesByAverageSizeWeight = 5,
        },
        AnalysisProfile.Full => new MemoryAnalysisOptions
        {
            TopTypesCount = 50,
            TopTypesBySizeWeight = 35,
            TopTypesByCountWeight = 30,
            TopTypesByLohWeight = 20,
            TopTypesByAverageSizeWeight = 15,
        },
        _ => new MemoryAnalysisOptions(),
    };

    public static MemoryAnalysisOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}
