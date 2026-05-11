using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using System.Diagnostics;

namespace DumpDetective.Cli.Pipeline;

using DumpDetective.Cli.Services;

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

    // ── Stage 3: RunAnalyzersPipelineStage ──────────────────────────────────
    public IReadOnlyList<AnalyzerRunResult> Runs { get; set; } = [];

    /// <summary>Elapsed time from pipeline start through the end of analyzer execution (stages 1–3).</summary>
    public TimeSpan AnalysisElapsed { get; set; }

    // ── Stage 4: GenerateFindingsStage ───────────────────────────────────────
    // Enriches Runs in-place with InsightFinding lists; no new properties required.

    // ── Stage 5: InsightEngine (post-pipeline) ───────────────────────────────
    /// <summary>Cross-cutting insight findings produced by <see cref="DumpDetective.Analysis.Insight.InsightEngine"/>.</summary>
    public IReadOnlyList<InsightFinding> Insights { get; set; } = [];

    // ── Stage 5: BuildReportStage ────────────────────────────────────────────
    public string RenderedReport { get; set; } = string.Empty;
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

    public void Dispose() => LoadContext?.Dispose();
}
