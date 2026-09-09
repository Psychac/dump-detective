using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
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
internal sealed class GcPauseAnalyzer : ITraceAnalyzer
{
    public string Name => "GcPauseAnalyzer";

    public IReadOnlySet<CacheSectionId> RequiredSections { get; } = new HashSet<CacheSectionId> { CacheSectionId.TraceGcEvents };

    public IReadOnlyList<Observation> Analyze(CacheContainerReader container, ArtifactId artifactId)
    {
        if (!container.TryOpenSection(CacheSectionId.TraceGcEvents, out Stream? sectionStream) || sectionStream is null)
            return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceGcEvents };
        var results = new List<Observation>();

        Span<byte> record = stackalloc byte[8 + 4 + 1 + 8 + 1 + 4 + 8];
        using (sectionStream)
        {
            while (true)
            {
                int read = sectionStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                int threadId = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
                byte reason = record[12];
                long pauseTicks = BinaryPrimitives.ReadInt64LittleEndian(record[13..]);
                bool hasGcData = record[21] != 0;
                int generation = BinaryPrimitives.ReadInt32LittleEndian(record[22..]);
                long heapBytes = BinaryPrimitives.ReadInt64LittleEndian(record[26..]);

                // GCSuspendEEReason (Microsoft.Diagnostics.Tracing.Parsers.Clr) has no direct SDK
                // representation — Measure is for quantities, not categories — so the raw enum
                // ordinal is carried as a Custom-unit measure rather than dropped. Still a raw fact,
                // not a judgment: no reason value is more or less "severe" here.
                var measures = new Dictionary<string, Measure>
                {
                    ["gc.suspend-reason"] = new Measure(reason, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["pause.duration"] = new Measure(
                        TimeSpan.FromTicks(pauseTicks).TotalMilliseconds, MeasureUnit.Milliseconds, MeasureSemantics.Duration),
                };
                if (hasGcData)
                {
                    measures["gc.generation"] = new Measure(generation, MeasureUnit.Count, MeasureSemantics.Absolute);
                    measures["heap.size"] = new Measure(heapBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute);
                }

                results.Add(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "gc.pause",
                    Subjects = [new ThreadRef { OsThreadId = (uint)threadId, Fidelity = MatchFidelity.Exact }],
                    When = new TemporalExtent
                    {
                        Kind = TemporalKind.Interval,
                        Start = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(timestampTicks), Confidence = AnchorConfidence.Exact },
                        End = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(timestampTicks + pauseTicks), Confidence = AnchorConfidence.Exact },
                    },
                    Measures = measures,
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
