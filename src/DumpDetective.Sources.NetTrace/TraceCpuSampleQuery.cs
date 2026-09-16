using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Trace-side implementation of the <c>trace.cpu-samples</c> capability — built for
/// <c>CpuHotspotAnalyzer</c>'s retyping onto <c>Sdk.Analysis.IAnalyzer</c>
/// (docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b). Ports the
/// pre-retyping analyzer's own address-range resolution unchanged, including
/// <see cref="MethodAddressIndex"/>'s cross-process-collision caveat (no per-process partitioning,
/// since <c>trace.methods</c> carries no <c>ProcessId</c> column) and the sample-stream loop's use
/// of a heap-allocated buffer rather than <c>stackalloc</c> — required here (unlike the
/// pre-retyping analyzer, which built its whole result list before returning) because this is now
/// an iterator method, and a <c>Span</c> can't be held live across a <c>yield return</c> boundary
/// (same reasoning <see cref="TraceMethodIndexReader"/> documents for itself).
/// </summary>
internal sealed class TraceCpuSampleQuery(CacheContainerReader container) : ITraceCpuSampleQuery
{
    public IEnumerable<CpuSampleAttribution> EnumerateSampleAttributions()
    {
        if (!container.ContainsSection(CacheSectionId.TraceCpuSamples))
            yield break;

        MethodAddressIndex? index = BuildAddressIndex(container);
        if (index is null)
            yield break;

        if (!container.TryOpenSection(CacheSectionId.TraceCpuSamples, out Stream? sampleStream) || sampleStream is null)
            yield break;

        var counts = new Dictionary<long, int>();
        var firstSeenTicks = new Dictionary<long, long>();
        var lastSeenTicks = new Dictionary<long, long>();

        byte[] record = new byte[8 + 4 + 4 + 8];
        using (sampleStream)
        {
            while (true)
            {
                int read = sampleStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                ulong instructionPointer = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16));

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

            yield return new CpuSampleAttribution(
                methodRef,
                count,
                TimeSpan.FromTicks(firstSeenTicks[methodId]),
                TimeSpan.FromTicks(lastSeenTicks[methodId]));
        }
    }

    /// <summary>
    /// Sorted-by-<c>StartAddress</c> address-range lookup built once per call from
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
