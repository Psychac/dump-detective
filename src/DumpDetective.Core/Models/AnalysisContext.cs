using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Core.Abstractions;

public class AnalysisContext
{
    // Intentional boundary decision (Phase 7): Core remains dump-runtime-aware.
    // AnalysisContext carries ClrRuntime/ClrHeap as shared execution substrate.
    public required ClrRuntime Runtime { get; init; }
    public ClrHeap Heap => Runtime.Heap;
    public required IHeapAnalysisCache Cache { get; init; }
    /// <summary>
    /// Typed per-run analysis options. Prefer this over the legacy type-keyed <see cref="Options"/> map.
    /// </summary>
    public AnalysisOptions AnalysisOptions { get; init; } = new();
    public DiagnosticsOptions Diagnostics { get; init; } = new();
    public IAnalysisDiagnosticsSink DiagnosticsSink { get; init; } = NullAnalysisDiagnosticsSink.Instance;

    /// <summary>
    /// Progress reporter injected by the pipeline per analyzer run.
    /// Analyzers report scan count, current phase, and optional detail.
    /// Null when running outside the pipeline (e.g. tests or direct <c>Analyze(heap)</c> calls).
    /// </summary>
    public IProgress<AnalyzerProgressReport>? Progress { get; set; }
}
