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

    public bool HasAnalyzerCollectionPolicy()
        => CollectAfterAnalyzerRun
           || CollectAfterAnalyzerRunEveryKAnalyzers > 0
           || CollectAfterAnalyzerRunWorkingSetThresholdBytes > 0;

    public bool ShouldCollectAfterAnalyzerRun(int completedAnalyzerCount, long workingSetBeforeAnalyzer, long workingSetAfterAnalyzer)
    {
        if (CollectAfterAnalyzerRun)
            return true;

        if (completedAnalyzerCount > 0
            && CollectAfterAnalyzerRunEveryKAnalyzers > 0
            && completedAnalyzerCount % CollectAfterAnalyzerRunEveryKAnalyzers == 0)
        {
            return true;
        }

        if (CollectAfterAnalyzerRunWorkingSetThresholdBytes > 0)
        {
            long workingSetDelta = workingSetAfterAnalyzer - workingSetBeforeAnalyzer;
            if (workingSetAfterAnalyzer >= CollectAfterAnalyzerRunWorkingSetThresholdBytes
                || workingSetDelta >= CollectAfterAnalyzerRunWorkingSetThresholdBytes)
            {
                return true;
            }
        }

        return false;
    }
}