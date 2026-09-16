using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// First 6b trace-fed analyzer (docs/refactor/modularity/phase-6-trace-source.md § Phase 6b).
/// Emits one <c>contention.episode</c> <see cref="Observation"/> per record in
/// <c>trace.contention</c>.
/// </summary>
/// <remarks>
/// Named <c>contention.episode</c>, not the design table's <c>contention.hotspot</c> — "hotspot"
/// is a severity/ranking claim (which episodes matter), and § 2a of
/// docs/refactor/modularity/observation-and-correlation-model.md already had to correct exactly
/// this failure mode for a different analyzer (<c>gc.pressure</c> → factual type, severity moved to
/// synthesis). A raw per-episode fact, not a verdict about which episodes are worth flagging, is
/// what belongs here; "hotspot" identification is a synthesis-rule output once one exists.
///
/// Retyped from <c>ITraceAnalyzer</c> onto <see cref="IAnalyzer"/> in
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b — the record
/// decoding this analyzer used to do itself now lives in <see cref="TraceContentionQuery"/>; this
/// class is now just the thin mapping from <see cref="ContentionEpisode"/> to
/// <see cref="Observation"/>.
/// </remarks>
[RequiresCapability(CapabilityVocabulary.TraceContentionEvents)]
internal sealed class ContentionAnalyzer : IAnalyzer
{
    public string Name => "ContentionAnalyzer";

    public string Category => "Trace";

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        ITraceContentionQuery? query = context.TraceContention;
        if (query is null)
            return;

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceContentionEvents };

        foreach (ContentionEpisode episode in query.EnumerateEpisodes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "contention.episode",
                Subjects = [episode.Thread],
                When = new TemporalExtent
                {
                    Kind = TemporalKind.Interval,
                    Start = new TimeAnchor { ProcessUptime = episode.StartProcessUptime, Confidence = AnchorConfidence.Exact },
                    End = new TimeAnchor { ProcessUptime = episode.StartProcessUptime + episode.Duration, Confidence = AnchorConfidence.Exact },
                },
                Measures = new Dictionary<string, Measure>
                {
                    ["contention.duration"] = new Measure(episode.Duration.TotalMilliseconds, MeasureUnit.Milliseconds, MeasureSemantics.Duration),
                },
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
