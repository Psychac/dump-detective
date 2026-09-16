using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Trace-side implementation of the <c>trace.gc-events</c> capability — built for
/// <c>GcPauseAnalyzer</c>'s retyping onto <c>Sdk.Analysis.IAnalyzer</c>
/// (docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b). Ports the
/// pre-retyping analyzer's own record decoding unchanged, aside from switching its read buffer from
/// <c>stackalloc</c> to heap-allocated — required now that this is an iterator method (see
/// <see cref="TraceCpuSampleQuery"/>'s own remarks for why).
/// </summary>
internal sealed class TraceGcPauseQuery(CacheContainerReader container) : ITraceGcPauseQuery
{
    public IEnumerable<GcPauseRecord> EnumeratePauses()
    {
        if (!container.TryOpenSection(CacheSectionId.TraceGcEvents, out Stream? sectionStream) || sectionStream is null)
            yield break;

        byte[] record = new byte[8 + 4 + 1 + 8 + 1 + 4 + 8];
        using (sectionStream)
        {
            while (true)
            {
                int read = sectionStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                int threadId = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8));
                byte reason = record[12];
                long pauseTicks = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(13));
                bool hasGcData = record[21] != 0;
                int generation = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(22));
                long heapBytes = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(26));

                yield return new GcPauseRecord(
                    new ThreadRef { OsThreadId = (uint)threadId, Fidelity = MatchFidelity.Exact },
                    TimeSpan.FromTicks(timestampTicks),
                    TimeSpan.FromTicks(pauseTicks),
                    reason,
                    hasGcData ? generation : null,
                    hasGcData ? heapBytes : null);
            }
        }
    }
}
