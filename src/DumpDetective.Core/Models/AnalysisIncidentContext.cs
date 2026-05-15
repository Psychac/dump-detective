using DumpDetective.Core.Configuration;

namespace DumpDetective.Core.Models;

internal sealed record AnalysisIncidentContext(
    string Mode,
    string DumpPath,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    string ReportFormat,
    string ReportAudience,
    string? ConfigPath,
    bool UsedConfigFile,
    bool DiagnosticMode,
    string IndexPrebuildMode,
    int ActiveAnalyzerCount,
    IReadOnlyList<string> ActiveAnalyzers,
    string? RuntimeVersion,
    string? RuntimeFlavor,
    string? GcMode,
    int? HeapCount,
    bool HeapCanWalk,
    bool IsTrendReport,
    double AnalysisElapsedSeconds,
    IReadOnlyList<TrendSnapshotContext>? TrendSnapshots = null,
    string? DumpSizeTierLabel = null,
    long? DumpFileSizeBytes = null);

internal sealed record TrendSnapshotContext(
    int Index,
    string DumpPath,
    DateTime GeneratedAtUtc,
    double ElapsedSeconds,
    int AnalyzerCount,
    int FindingCount,
    bool IsBaseline,
    bool IsCurrent);