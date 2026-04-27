using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Options;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Cli.Services;

internal sealed record ResolvedExecutionOptions(
    string DumpPath,
    string OutputPath,
    string? BaselineDumpPath,
    IReadOnlyList<string>? TrendDumpPaths,
    MemoryLeakOptions MemoryLeak,
    ReferenceChainOptions ReferenceChain,
    EventLeakOptions EventLeak,
    DiagnosticsOptions Diagnostics,
    ReportOptions Report,
    CollectionAnalyzerOptions Collection,
    string? ConfigPath,
    bool UsedConfigFile,
    IReadOnlyCollection<string> IncludeAnalyzers,
    IReadOnlyCollection<string> ExcludeAnalyzers,
    bool DiagnosticMode,
    HeapIndexPrebuildMode IndexPrebuildMode);
