using DumpDetective.Sdk.Observations;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Discards everything. Phase 5 (observation emission) hasn't started — no migrated analyzer emits
/// <see cref="Observation"/>s yet — but <c>Sdk.Analysis.AnalysisContext.Observations</c> is
/// <c>required</c>, so <see cref="LegacyAnalysisContextTranslator"/> needs a sink to hand it. An
/// analyzer that ever does call <see cref="EmitAsync"/> through this sink is silently dropping data,
/// not merely deferring it — safe today only because no migrated analyzer does.
/// </summary>
internal sealed class NoOpObservationSink : IObservationSink
{
    public static readonly NoOpObservationSink Instance = new();

    private NoOpObservationSink() { }

    public ValueTask EmitAsync(Observation observation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
