namespace DumpDetective.Core.Models;

internal sealed record TrendSnapshotContext(
    int Index,
    string DumpPath,
    DateTime GeneratedAtUtc,
    double ElapsedSeconds,
    int AnalyzerCount,
    int FindingCount,
    bool IsBaseline,
    bool IsCurrent,
    long? DumpFileSizeBytes = null,
    DateTime? DumpCapturedAtUtc = null);