using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// First 6b trace-fed analyzer covering CPU (docs/refactor/modularity/phase-6-trace-source.md
/// § Phase 6b). Emits one <c>cpu.sample-attribution</c> <see cref="Observation"/> per method that at
/// least one <c>trace.cpu-samples</c> leaf sample resolved to — no inclusive/exclusive call-tree
/// breakdown, since that needs the full call stack this analyzer's own section deliberately doesn't
/// carry (see <c>CacheSectionId.TraceCpuSamples</c>'s remarks). "Exclusive-only, leaf-attributed" is
/// still real per-method CPU-time evidence, not a placeholder.
/// </summary>
/// <remarks>
/// Named <c>cpu.sample-attribution</c>, not the design table's <c>cpu.hotspot</c> — same purity
/// correction already applied to <c>ContentionAnalyzer</c> (see its own remarks): "hotspot" is a
/// ranking/severity claim, not a raw fact. "N samples landed in this method" is the fact; deciding
/// which methods are hot enough to report is a synthesis-rule job (Phase 5, not built).
///
/// Retyped from <c>ITraceAnalyzer</c> onto <see cref="IAnalyzer"/> in
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b — the address-range
/// resolution this analyzer used to do itself now lives in <see cref="TraceCpuSampleQuery"/>; this
/// class is now just the thin mapping from <see cref="CpuSampleAttribution"/> to
/// <see cref="Observation"/>.
/// </remarks>
[RequiresCapability(CapabilityVocabulary.TraceCpuSamples)]
internal sealed class CpuHotspotAnalyzer : IAnalyzer
{
    public string Name => "CpuHotspotAnalyzer";

    public string Category => "Trace";

    public async ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        ITraceCpuSampleQuery? query = context.TraceCpuSamples;
        if (query is null)
            return;

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceCpuSamples };

        foreach (CpuSampleAttribution attribution in query.EnumerateSampleAttributions())
        {
            cancellationToken.ThrowIfCancellationRequested();

            await context.Observations.EmitAsync(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "cpu.sample-attribution",
                Subjects = [attribution.Method],
                When = new TemporalExtent
                {
                    Kind = TemporalKind.Interval,
                    Start = new TimeAnchor { ProcessUptime = attribution.FirstSeenProcessUptime, Confidence = AnchorConfidence.Exact },
                    End = new TimeAnchor { ProcessUptime = attribution.LastSeenProcessUptime, Confidence = AnchorConfidence.Exact },
                },
                Measures = new Dictionary<string, Measure>
                {
                    ["cpu.sample-count"] = new Measure(attribution.SampleCount, MeasureUnit.Count, MeasureSemantics.Absolute),
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
