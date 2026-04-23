namespace DumpDetective.Core.Options;

public sealed class DiagnosticsOptions
{
    public bool EnableMemoryDiagnostics { get; init; }
    public bool EnablePerformanceDiagnostics { get; init; }
    public bool ContinueOnAnalyzerFailure { get; init; } = true;
}