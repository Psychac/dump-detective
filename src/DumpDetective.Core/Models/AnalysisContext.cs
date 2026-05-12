using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public class AnalysisContext
{
    public required ClrRuntime Runtime { get; init; }
    public ClrHeap Heap => Runtime.Heap;
    public required IHeapAnalysisCache Cache { get; init; }
    public DiagnosticsOptions Diagnostics { get; init; } = new();
    /// <summary>
    /// Per-analyzer options, keyed by the option type itself.
    /// Use <c>context.GetOption&lt;T&gt;()</c> (Analysis project extension) to read safely with default fallback.
    /// Populated by the CLI pipeline from <see cref="DumpDetective.Cli.Services.ResolvedExecutionOptions"/>.
    /// </summary>
    public IReadOnlyDictionary<Type, object?> Options { get; init; } = new Dictionary<Type, object?>();
    public IAnalysisDiagnosticsSink DiagnosticsSink { get; init; } = NullAnalysisDiagnosticsSink.Instance;

    /// <summary>
    /// Progress reporter injected by the pipeline per analyzer run.
    /// Analyzers report scan count, current phase, and optional detail.
    /// Null when running outside the pipeline (e.g. tests or direct <c>Analyze(heap)</c> calls).
    /// </summary>
    public IProgress<AnalyzerProgressReport>? Progress { get; set; }
}
