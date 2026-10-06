using DumpDetective.Sdk.Synthesis;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Core.Models;
using System.Diagnostics;
using DumpDetective.Cli.Services;
using DumpDetective.Cli.Models;

namespace DumpDetective.Cli.Pipeline;

 
/// <summary>
/// Mutable state bag that carries data between pipeline stages for a single dump analysis run.
/// Owns the <see cref="DumpLoadContext"/> and disposes it when the pipeline completes.
/// </summary>
internal sealed class SingleDumpPipelineState : IDisposable
{
    // ── Inputs (set before pipeline starts) ─────────────────────────────────
    public required ResolvedExecutionOptions Resolved { get; init; }
    public required IReadOnlyList<IAnalyzer> AllAnalyzers { get; init; }
    public required IReadOnlyList<IAnalyzer> ActiveAnalyzers { get; init; }

    // ── Stage 1: LoadDumpStage ───────────────────────────────────────────────
    public DumpLoadContext? LoadContext { get; set; }

    // ── Stage 2: BuildHeapIndexStage ────────────────────────────────────────
    /// <summary>Build-time interface — used by <see cref="Stages.BuildHeapIndexStage"/> to construct the index.</summary>
    public IHeapIndexBuilder? HeapIndexBuilder { get; set; }
    /// <summary>Read-only cache interface — used by <see cref="Stages.RunAnalyzersPipelineStage"/> as the analyzer <c>Cache</c> contract.</summary>
    public IHeapAnalysisCache? HeapCache { get; set; }
    public HeapIndexBuildResult? HeapIndex { get; set; }
    /// <summary>
    /// Analysis context and pipeline built by <see cref="Stages.BuildHeapIndexStage"/> so the
    /// shared heap-index/thread-stack scan passes run under that stage's header/timer instead of
    /// under "Run analyzers"; reused as-is by <see cref="Stages.RunAnalyzersPipelineStage"/>.
    /// </summary>
    public Analysis.Pipeline.RuntimeAnalysisContext? Context { get; set; }
    public Analysis.Pipeline.AnalysisPipeline? Pipeline { get; set; }

    // ── Stage 3: RunAnalyzersPipelineStage ──────────────────────────────────
    public IReadOnlyList<AnalyzerRunResult> Runs { get; set; } = [];

    /// <summary>
    /// Every <see cref="DumpDetective.Sdk.Observations.Observation"/> emitted by this run's
    /// analyzers, captured from <see cref="Context"/>'s shared <c>ObservationSink</c> once analyzers
    /// finish running (Phase 5 step 1, docs/refactor/modularity/phase-5-observations-synthesis.md).
    /// Consumed by <see cref="DumpDetective.Reporting.Services.SynthesisFindingEnricher"/> (Phase 5 step 2) to
    /// override <see cref="Runs"/>' findings for any analyzer with a migrated <c>ISynthesisRule</c>
    /// — currently just <c>ArraySynthesisRule</c>/"Array Analysis".
    /// </summary>
    public IReadOnlyList<DumpDetective.Sdk.Observations.Observation> Observations { get; set; } = [];

    /// <summary>Elapsed time from pipeline start through the end of analyzer execution (stages 1–3).</summary>
    public TimeSpan AnalysisElapsed { get; set; }

    /// <summary>
    /// Cross-analyzer synthesis findings that belong to no single analyzer run — see
    /// <see cref="DumpDetective.Reporting.Services.SynthesisEnrichment.UnattachedFindings"/>.
    /// </summary>
    public IReadOnlyList<Claim> Insights { get; set; } = [];

    /// <summary>Observation lineage and confidence breakdown per finding fingerprint, for the report document.</summary>
    public IReadOnlyDictionary<string, DumpDetective.Reporting.Models.FindingLineage> FindingLineage { get; set; }
        = new Dictionary<string, DumpDetective.Reporting.Models.FindingLineage>();

    // ── Stage 5: BuildReportStage ────────────────────────────────────────────
    public DumpDetective.Reporting.Models.AnalysisReportDocument? ReportDocument { get; set; }
    public DumpDetective.Core.Models.AnalysisIncidentContext? IncidentContext { get; set; }

    // ── Shared ───────────────────────────────────────────────────────────────
    /// <summary>Stopwatch started when the state is created; used to compute cumulative elapsed across stages.</summary>
    public Stopwatch PipelineStopwatch { get; } = Stopwatch.StartNew();

    /// <summary>
    /// Per-stage memory snapshots. Populated by <see cref="StagedPipelineRunner"/> when
    /// <c>DiagnosticsOptions.EnableMemoryDiagnostics</c> is set. Empty otherwise.
    /// Reuses <see cref="AnalyzerMemoryStats"/> — the same four counters apply to any timed scope.
    /// </summary>
    public List<(string StageName, AnalyzerMemoryStats Stats)> StageMemoryStats { get; } = [];

    /// <summary>
    /// Set when the wrapper stage has already populated detailed load/scan/analyze memory
    /// snapshots, so the outer pipeline runner should not add a combined wrapper row.
    /// </summary>
    public bool HasDetailedStageMemoryStats { get; set; }

    public void Dispose()
    {
        // HeapCache isn't IDisposable on its interface (most cache implementations don't need it);
        // duck-type so the disk-backed reverse-index reader's memory-mapped views still get
        // released promptly instead of waiting on finalization.
        (HeapCache as IDisposable)?.Dispose();
        LoadContext?.Dispose();
    }
}
