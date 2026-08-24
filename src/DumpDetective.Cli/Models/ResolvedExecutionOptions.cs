using DumpDetective.Core.Options;

namespace DumpDetective.Cli.Models;

internal sealed record ResolvedExecutionOptions(
    string DumpPath,
    string OutputPath,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    RetentionOptions MemoryLeak,
    ReferenceChainOptions ReferenceChain,
    EventLeakOptions EventLeak,
    DiagnosticsOptions Diagnostics,
    ReportOptions Report,
    CrashAnalysisOptions Crash,
    AsyncTaskAnalysisOptions AsyncTaskAnalysis,
    AsyncStateMachineAnalysisOptions AsyncStateMachineAnalysis,
    ArrayAnalysisOptions ArrayAnalysis,
    BoxingAnalysisOptions BoxingAnalysis,
    CollectionAnalysisOptions Collection,
    StringAnalysisOptions StringAnalysis,
    AllocationPatternAnalysisOptions AllocationPatternAnalysis,
    ThreadStackClusterAnalysisOptions ThreadStackClusterAnalysis,
    GCGenerationAnalysisOptions GCGenerationAnalysis,
    SegmentReservationAnalysisOptions SegmentReservationAnalysis,
    ThreadAnalysisOptions ThreadAnalysis,
    HangAnalysisOptions HangAnalysis,
    JitAnalysisOptions JitAnalysis,
    WeakReferenceAnalysisOptions WeakReferenceAnalysis,
    ModuleAnalysisOptions ModuleAnalysis,
    GCHandleAnalysisOptions GCHandleAnalysis,
    StaticRootLeakAnalysisOptions StaticRootLeakAnalysis,
    MemoryAnalysisOptions MemoryAnalysis,
    string? ConfigPath,
    bool UsedConfigFile,
    IReadOnlyCollection<string> IncludeAnalyzers,
    IReadOnlyCollection<string> ExcludeAnalyzers,
    bool DiagnosticMode)
{
    public ExecutionPolicy ExecutionPolicy { get; init; } = DumpDetective.Core.Options.ExecutionPolicy.Default;
    public string? CacheDirectory { get; init; }
}
