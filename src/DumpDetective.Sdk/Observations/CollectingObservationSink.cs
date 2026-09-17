namespace DumpDetective.Sdk.Observations;

/// <summary>
/// Accumulates every emitted <see cref="Observation"/> into a list. Originally the trace side's
/// sink only (docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b);
/// relocated into the SDK for Phase 5 step 1
/// (docs/refactor/modularity/phase-5-observations-synthesis.md), which gives the dump pipeline the
/// same real (non-<c>NoOp</c>) sink instead of a second copy. Still no <c>ObservationStore</c> —
/// disk-backed accumulation is deferred until a real-dump run shows this doesn't hold (see
/// observation-and-correlation-model.md § 7's open question), consistent with this project's
/// measure-first-then-design convention.
/// </summary>
public sealed class CollectingObservationSink : IObservationSink
{
    private readonly List<Observation> _observations = [];

    public IReadOnlyList<Observation> Observations => _observations;

    public ValueTask EmitAsync(Observation observation, CancellationToken cancellationToken = default)
    {
        _observations.Add(observation);
        return ValueTask.CompletedTask;
    }
}
