using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Accumulates every emitted <see cref="Observation"/> into a list — <c>TraceAnalysisRunner</c>'s
/// sink for the retyped trace analyzers
/// (docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b). The first real
/// (non-<c>NoOp</c>) <see cref="IObservationSink"/> in the codebase: no dump-side analyzer emits a
/// real <see cref="Observation"/> yet (Phase 5 hasn't started), but the trace side already needs to
/// hand its observations to <c>TraceOrchestrationService</c>'s console summary and
/// <c>TraceReportWriter</c>, so a real sink — not <c>NoOpObservationSink</c> — is a genuine, not
/// speculative, need here.
/// </summary>
internal sealed class CollectingObservationSink : IObservationSink
{
    private readonly List<Observation> _observations = [];

    public IReadOnlyList<Observation> Observations => _observations;

    public ValueTask EmitAsync(Observation observation, CancellationToken cancellationToken = default)
    {
        _observations.Add(observation);
        return ValueTask.CompletedTask;
    }
}
