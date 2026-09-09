using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
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
/// </remarks>
internal sealed class ContentionAnalyzer : ITraceAnalyzer
{
    public string Name => "ContentionAnalyzer";

    public IReadOnlySet<CacheSectionId> RequiredSections { get; } = new HashSet<CacheSectionId> { CacheSectionId.TraceContention };

    public IReadOnlyList<Observation> Analyze(CacheContainerReader container, ArtifactId artifactId)
    {
        if (!container.TryOpenSection(CacheSectionId.TraceContention, out Stream? sectionStream) || sectionStream is null)
            return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceContentionEvents };
        var results = new List<Observation>();

        Span<byte> record = stackalloc byte[8 + 8 + 4 + 1];
        using (sectionStream)
        {
            while (true)
            {
                int read = sectionStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long startTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                long durationTicks = BinaryPrimitives.ReadInt64LittleEndian(record[8..]);
                int threadId = BinaryPrimitives.ReadInt32LittleEndian(record[16..]);

                results.Add(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "contention.episode",
                    Subjects = [new ThreadRef { OsThreadId = (uint)threadId, Fidelity = MatchFidelity.Exact }],
                    When = new TemporalExtent
                    {
                        Kind = TemporalKind.Interval,
                        Start = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(startTicks), Confidence = AnchorConfidence.Exact },
                        End = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(startTicks + durationTicks), Confidence = AnchorConfidence.Exact },
                    },
                    Measures = new Dictionary<string, Measure>
                    {
                        ["contention.duration"] = new Measure(
                            TimeSpan.FromTicks(durationTicks).TotalMilliseconds, MeasureUnit.Milliseconds, MeasureSemantics.Duration),
                    },
                    Provenance = new Provenance
                    {
                        Artifact = artifactId,
                        AnalyzerKey = Name,
                        CapabilitiesUsed = capabilitiesUsed,
                        Fidelity = FidelityLevel.Full,
                    },
                    Confidence = 1.0,
                });
            }
        }

        return results;
    }
}
