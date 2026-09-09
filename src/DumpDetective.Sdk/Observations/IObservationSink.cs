namespace DumpDetective.Sdk.Observations;

/// <summary>
/// The channel analyzers emit observations through. Deliberately accepts one
/// <see cref="Observation"/> at a time rather than <c>IReadOnlyList&lt;Observation&gt;</c> — the
/// project's no-full-materialization rule applies to observations exactly as it does to heap
/// objects. An analyzer that wants to emit a million observations has a modeling error, not a
/// performance problem to solve later. See docs/refactor/modularity/phase-1-contracts-sdk.md.
/// </summary>
public interface IObservationSink
{
    ValueTask EmitAsync(Observation observation, CancellationToken cancellationToken = default);
}
