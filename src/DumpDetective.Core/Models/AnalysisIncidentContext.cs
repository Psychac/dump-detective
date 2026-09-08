using DumpDetective.Core.Configuration;

namespace DumpDetective.Core.Models;

internal sealed record AnalysisIncidentContext(
    string Mode,
    string DumpPath,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    string ReportFormat,
    string? ConfigPath,
    bool UsedConfigFile,
    bool DiagnosticMode,
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
    long? DumpFileSizeBytes = null,
    DateTime? DumpCapturedAtUtc = null);
