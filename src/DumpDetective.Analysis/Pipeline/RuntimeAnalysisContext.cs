using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Query;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Execution-time context threaded through the analyzer pipeline. Used to run shared heap/thread
/// scans and to build each analyzer's own <see cref="Sdk.Analysis.AnalysisContext"/> via
/// <see cref="AnalysisContextBuilder"/> — never passed to an analyzer's <c>AnalyzeAsync</c> directly.
/// </summary>
internal sealed class RuntimeAnalysisContext
{
    public required ClrRuntime Runtime { get; init; }
    public ClrHeap Heap => Runtime.Heap;
    public required IHeapAnalysisCache Cache { get; init; }
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

    /// <summary>
    /// Facade wrapping <see cref="Runtime"/> and <see cref="Heap"/>
    /// with a shared per-session <c>MethodTable → ClrType</c> cache.
    /// Populated by the pipeline before any analyzer runs.
    /// May be <c>null</c> in unit tests that supply a lightweight <see cref="IHeapAnalysisCache"/> stub
    /// without a real <see cref="Microsoft.Diagnostics.Runtime.ClrRuntime"/>.
    /// </summary>
    public RuntimeFacade? RuntimeFacade { get; init; }

    /// <summary>
    /// Convenience accessor that exposes the build-time interface for the heap cache.
    /// Returns null when <see cref="Cache"/> does not implement <see cref="IHeapIndexBuilder"/>
    /// (e.g., in unit tests that supply a lightweight <see cref="IHeapAnalysisCache"/> stub).
    /// </summary>
    public IHeapIndexBuilder? HeapIndexBuilder => Cache as IHeapIndexBuilder;

    /// <summary>
    /// Query engine that operates on the pre-built index — never on the raw heap.
    /// Lazily instantiated on first access; <c>null</c> in unit tests that do not supply a real runtime.
    /// </summary>
    public IQueryEngine? Query => _query ??= new QueryEngine(Cache, Heap);
    private IQueryEngine? _query;
}
