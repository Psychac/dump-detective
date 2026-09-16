using DumpDetective.Sdk.Observations;
using DumpDetective.Sources.NetTrace;

namespace DumpDetective.Cli.Pipeline;

/// <summary>
/// Mutable state bag carrying data between a trace run's pipeline nodes — the trace-side analog of
/// <see cref="SingleDumpPipelineState"/>. Introduced in
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5f so a trace artifact's
/// nodes can share one <see cref="Orchestration.Graph.PipelineNodeContext"/> with a dump artifact's
/// nodes for a combined session, instead of running through the separate procedural
/// <c>TraceAnalysisRunner</c> path step 5d used as an interim measure.
/// </summary>
internal sealed class TracePipelineState : IDisposable
{
    // ── Inputs (set before pipeline starts) ─────────────────────────────────
    public required string TracePath { get; init; }
    public required string? OutputPath { get; init; }

    // ── BuildTraceIndexNode ──────────────────────────────────────────────────
    public TraceContainer? Container { get; set; }

    // ── RunTraceAnalyzersNode ────────────────────────────────────────────────
    public IReadOnlyList<Observation> Observations { get; set; } = [];

    public void Dispose() => Container?.DeleteBackingFile();
}
