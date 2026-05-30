namespace DumpDetective.Core.Options;

public sealed class DiagnosticsOptions
{
    public bool EnableMemoryDiagnostics { get; init; }
    public bool EnablePerformanceDiagnostics { get; init; }
    public bool ContinueOnAnalyzerFailure { get; init; } = true;
    // When true, the pipeline will run a best-effort GC pass after each analyzer completes.
    // Default: false. Prefer explicit disposal and pooling over relying on this flag.
    public bool CollectAfterAnalyzerRun { get; init; }
    public int CollectAfterAnalyzerRunEveryKAnalyzers { get; init; }
    public long CollectAfterAnalyzerRunWorkingSetThresholdBytes { get; init; }
    public bool CompactLargeObjectHeapAfterAnalyzerCollection { get; init; } = true;
}