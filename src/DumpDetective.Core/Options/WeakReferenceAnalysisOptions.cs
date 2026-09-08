namespace DumpDetective.Core.Options;

public sealed class WeakReferenceAnalysisOptions
{
    // Whether to also write raw weak/dependent-handle samples to disk as NDJSON+gzip and a JSON
    // summary artifact. An output-format toggle, not an exactness knob — deliberately left here
    // rather than moved to ReportOptions per D6 (§11.2), a cross-cutting change shared with
    // StringAnalysisOptions.ProduceRawExports/ThreadStackClusterAnalysisOptions.ProduceClusterExports;
    // not executed in this pass.
    public bool ProduceRawExports { get; init; } = false;
}
