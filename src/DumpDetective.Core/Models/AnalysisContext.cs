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

    /// <summary>
    /// Optional hook a participant can call from <c>BeforeHeapIndexScan</c> (or similar setup)
    /// to name the inner phase it's currently doing — e.g. "building publisher registry" — while
    /// it does slow work with no natural per-item progress hook of its own. Set by the dispatcher
    /// running that setup, which folds the latest value into its own live progress reporting;
    /// null when there's nowhere to render it (e.g. tests calling Analyze directly). Cheap enough
    /// to call a handful of times per run; not for hot per-entry loops.
    /// </summary>
    public Action<string>? ReportSubPhase { get; set; }

    /// <summary>
    /// Completed run results from every non-deferred analyzer, populated by the pipeline before
    /// running <see cref="IDeferredAnalyzer"/> analyzers. Lets deferred analyzers consume finished
    /// results post-hoc via <c>AnalyzerRunResultsExtensions.GetResult&lt;T&gt;</c> without depending
    /// on pipeline execution order. Null when running outside the pipeline.
    /// </summary>
    internal IReadOnlyList<AnalyzerRunResult>? CompletedRunResults { get; set; }
}
