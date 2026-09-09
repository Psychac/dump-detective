using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
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
/// </remarks>
internal sealed class CpuHotspotAnalyzer : ITraceAnalyzer
{
    public string Name => "CpuHotspotAnalyzer";

    public IReadOnlySet<CacheSectionId> RequiredSections { get; } =
        new HashSet<CacheSectionId> { CacheSectionId.TraceMethods, CacheSectionId.TraceCpuSamples };

    public IReadOnlyList<Observation> Analyze(CacheContainerReader container, ArtifactId artifactId)
    {
        if (!container.ContainsSection(CacheSectionId.TraceCpuSamples))
            return [];

        MethodAddressIndex? index = BuildAddressIndex(container);
        if (index is null)
            return [];

        if (!container.TryOpenSection(CacheSectionId.TraceCpuSamples, out Stream? sampleStream) || sampleStream is null)
            return [];

        var counts = new Dictionary<long, int>();
        var firstSeenTicks = new Dictionary<long, long>();
        var lastSeenTicks = new Dictionary<long, long>();

        Span<byte> record = stackalloc byte[8 + 4 + 4 + 8];
        using (sampleStream)
        {
            while (true)
            {
                int read = sampleStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                ulong instructionPointer = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);

                if (index.TryResolve(instructionPointer, out long methodId))
                {
                    counts[methodId] = counts.GetValueOrDefault(methodId) + 1;
                    if (!firstSeenTicks.TryGetValue(methodId, out long first) || timestampTicks < first)
                        firstSeenTicks[methodId] = timestampTicks;
                    if (!lastSeenTicks.TryGetValue(methodId, out long last) || timestampTicks > last)
                        lastSeenTicks[methodId] = timestampTicks;
                }
            }
        }

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.TraceCpuSamples };
        var results = new List<Observation>(counts.Count);
        foreach ((long methodId, int count) in counts)
        {
            TraceMethodRecord method = index.Records[methodId];
            var declaringType = new TypeRef { CanonicalName = method.DeclaringTypeCanonicalName, Fidelity = method.Fidelity };
            var methodRef = new MethodRef
            {
                DeclaringType = declaringType,
                Name = method.MethodName,
                // MethodRef.NormalizedSignature wants a canonicalized parameter list; trace.methods
                // only carries the raw IL-notation signature (its own already-documented
                // simplification — see TraceMethodIndexWriter's remarks). Passed through as-is
                // rather than guessed at a second time here.
                NormalizedSignature = method.RawSignature,
                MethodToken = method.Token,
                Fidelity = method.Fidelity,
            };

            results.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "cpu.sample-attribution",
                Subjects = [methodRef],
                When = new TemporalExtent
                {
                    Kind = TemporalKind.Interval,
                    Start = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(firstSeenTicks[methodId]), Confidence = AnchorConfidence.Exact },
                    End = new TimeAnchor { ProcessUptime = TimeSpan.FromTicks(lastSeenTicks[methodId]), Confidence = AnchorConfidence.Exact },
                },
                Measures = new Dictionary<string, Measure>
                {
                    ["cpu.sample-count"] = new Measure(count, MeasureUnit.Count, MeasureSemantics.Absolute),
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

        return results;
    }

    /// <summary>
    /// Sorted-by-<c>StartAddress</c> address-range lookup built once per run from
    /// <c>trace.methods</c> — see <c>CpuSampleIndexer</c>'s remarks for the cross-process-collision
    /// caveat this index inherits (it has no per-process partitioning to give, since
    /// <c>trace.methods</c> carries no <c>ProcessId</c> column).
    /// </summary>
    private sealed class MethodAddressIndex
    {
        private readonly ulong[] _sortedStarts;
        private readonly long[] _methodIdsByStart;

        public IReadOnlyDictionary<long, TraceMethodRecord> Records { get; }

        public MethodAddressIndex(ulong[] sortedStarts, long[] methodIdsByStart, IReadOnlyDictionary<long, TraceMethodRecord> records)
        {
            _sortedStarts = sortedStarts;
            _methodIdsByStart = methodIdsByStart;
            Records = records;
        }

        public bool TryResolve(ulong instructionPointer, out long methodId)
        {
            methodId = 0;
            int idx = Array.BinarySearch(_sortedStarts, instructionPointer);
            if (idx < 0)
                idx = ~idx - 1;
            if (idx < 0)
                return false;

            long candidateId = _methodIdsByStart[idx];
            TraceMethodRecord candidate = Records[candidateId];
            if (instructionPointer >= candidate.StartAddress && instructionPointer < candidate.StartAddress + (ulong)candidate.Size)
            {
                methodId = candidateId;
                return true;
            }

            return false;
        }
    }

    private static MethodAddressIndex? BuildAddressIndex(CacheContainerReader container)
    {
        if (!container.TryOpenSection(CacheSectionId.TraceMethods, out Stream? methodStream) || methodStream is null)
            return null;

        var records = new Dictionary<long, TraceMethodRecord>();
        var entries = new List<(ulong Start, long MethodId)>();

        using (methodStream)
        {
            foreach (TraceMethodRecord method in TraceMethodIndexReader.ReadAll(methodStream))
            {
                if (method.Size <= 0)
                    continue; // No resolvable address range — nothing an IP could ever fall inside.

                records[method.MethodId] = method;
                entries.Add((method.StartAddress, method.MethodId));
            }
        }

        if (entries.Count == 0)
            return null;

        entries.Sort((a, b) => a.Start.CompareTo(b.Start));
        var sortedStarts = new ulong[entries.Count];
        var methodIdsByStart = new long[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            sortedStarts[i] = entries[i].Start;
            methodIdsByStart[i] = entries[i].MethodId;
        }

        return new MethodAddressIndex(sortedStarts, methodIdsByStart, records);
    }
}
