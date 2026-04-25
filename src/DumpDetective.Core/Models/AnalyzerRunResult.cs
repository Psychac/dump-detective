namespace DumpDetective.Core.Models;

internal enum AnalyzerExecutionStatus
{
    Success,
    Failed,
    Skipped,
    Canceled
}

internal sealed record AnalyzerRunResult(
    string AnalyzerName,
    AnalyzerExecutionStatus Status,
    TimeSpan Duration,
    AnalyzerDomainResult? Result,
    string? ErrorMessage,
    string? ErrorType,
    IReadOnlyList<InsightFinding>? Findings = null,
    int FindingCount = 0,
    int WarningCount = 0,
    long ObjectScanCount = 0,
    long CacheHits = 0,
    long CacheMisses = 0)
{
    /// <summary>Generated findings for this run. Populated by <see cref="DumpDetective.Analysis.FindingGenerators"/> after the analyzer completes.</summary>
    public IReadOnlyList<InsightFinding> Findings { get; init; } = Findings ?? [];
}
