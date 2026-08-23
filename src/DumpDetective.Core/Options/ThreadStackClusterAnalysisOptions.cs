namespace DumpDetective.Core.Options;

/// <summary>
/// Configurable limits for <c>ThreadStackClusterAnalyzer</c>.
/// </summary>
public sealed class ThreadStackClusterAnalysisOptions
{
    // Semantic threshold (Category 5): a cluster of size 1 is always a singleton, never
    // "interesting" on its own — kept as the single Balanced value, not tier-varied.
    public int MinClusterSize { get; init; } = 1;

    // Whether to also write the filtered cluster set to disk as JSON/NDJSON exports for offline
    // inspection. An output-format toggle, not an exactness knob — deliberately left here rather
    // than moved to ReportOptions per D6 (§11.2), which decided the destination but is a
    // cross-cutting change shared with StringAnalysisOptions.ProduceRawExports/
    // WeakReferenceAnalysisOptions.ProduceRawExports; not executed in this pass.
    public bool ProduceClusterExports { get; init; } = false;
}
