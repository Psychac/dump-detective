using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// First 6b trace-fed analyzer (docs/refactor/modularity/phase-6-trace-source.md § Phase 6b).
/// Emits one <c>gc.pause</c> <see cref="Observation"/> per record in <c>trace.gcevents</c> — no
/// aggregation, banding, or severity here; "count, total/max pause" is a synthesis-rule output
/// (Phase 5, not yet built), not something this analyzer computes itself. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 2a for why: an observation's
/// job is the raw fact, not a judgment about it.
/// </summary>
/// <remarks>
/// Retyped from <c>ITraceAnalyzer</c> onto <see cref="IAnalyzer"/> in
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b — the record
/// decoding this analyzer used to do itself now lives in <see cref="TraceGcPauseQuery"/>; this
/// class is now just the thin mapping from <see cref="GcPauseRecord"/> to <see cref="Observation"/>.
/// </remarks>
[RequiresCapability(CapabilityVocabulary.TraceGcEvents)]
internal sealed class GcPauseAnalyzer : IAnalyzer
{
    public string Name => "GcPauseAnalyzer";

    public string Category => "Trace";

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        ITraceGcPauseQuery? query = context.TraceGcPauses;
        if (query is null)
            return;

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceGcEvents };

        foreach (GcPauseRecord pause in query.EnumeratePauses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GCSuspendEEReason (Microsoft.Diagnostics.Tracing.Parsers.Clr) has no direct SDK
            // representation — Measure is for quantities, not categories — so the raw enum
            // ordinal is carried as a Custom-unit measure rather than dropped. Still a raw fact,
            // not a judgment: no reason value is more or less "severe" here.
            var measures = new Dictionary<string, Measure>
            {
                ["gc.suspend-reason"] = new Measure(pause.Reason, MeasureUnit.Custom, MeasureSemantics.Absolute),
                ["pause.duration"] = new Measure(pause.Duration.TotalMilliseconds, MeasureUnit.Milliseconds, MeasureSemantics.Duration),
            };
            if (pause.Generation is int generation && pause.HeapSizeBytes is long heapBytes)
            {
                measures["gc.generation"] = new Measure(generation, MeasureUnit.Count, MeasureSemantics.Absolute);
                measures["heap.size"] = new Measure(heapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute);
            }

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "gc.pause",
                Subjects = [pause.Thread],
                When = new TemporalExtent
                {
                    Kind = TemporalKind.Interval,
                    Start = new TimeAnchor { ProcessUptime = pause.StartProcessUptime, Confidence = AnchorConfidence.Exact },
                    End = new TimeAnchor { ProcessUptime = pause.StartProcessUptime + pause.Duration, Confidence = AnchorConfidence.Exact },
                },
                Measures = measures,
                Provenance = new Provenance
                {
                    Artifact = context.ArtifactId!.Value, // guaranteed set — see AnalysisContext.ArtifactId's remarks
                    AnalyzerKey = Name,
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            }, cancellationToken);
        }
    }
}
