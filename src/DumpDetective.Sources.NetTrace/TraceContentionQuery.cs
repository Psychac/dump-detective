using System.Buffers.Binary;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Trace-side implementation of the <c>trace.contention-events</c> capability — built for
/// <c>ContentionAnalyzer</c>'s retyping onto <c>Sdk.Analysis.IAnalyzer</c>
/// (docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b). Ports the
/// pre-retyping analyzer's own record decoding unchanged, aside from switching its read buffer from
/// <c>stackalloc</c> to heap-allocated — see <see cref="TraceCpuSampleQuery"/>'s own remarks for why.
/// </summary>
internal sealed class TraceContentionQuery(CacheContainerReader container) : ITraceContentionQuery
{
    public IEnumerable<ContentionEpisode> EnumerateEpisodes()
    {
        if (!container.TryOpenSection(CacheSectionId.TraceContention, out Stream? sectionStream) || sectionStream is null)
            yield break;

        byte[] record = new byte[8 + 8 + 4 + 1];
        using (sectionStream)
        {
            while (true)
            {
                int read = sectionStream.ReadAtLeast(record, record.Length, throwOnEndOfStream: false);
                if (read < record.Length)
                    break;

                long startTicks = BinaryPrimitives.ReadInt64LittleEndian(record);
                long durationTicks = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(8));
                int threadId = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(16));

                yield return new ContentionEpisode(
                    new ThreadRef { OsThreadId = (uint)threadId, Fidelity = MatchFidelity.Exact },
                    TimeSpan.FromTicks(startTicks),
                    TimeSpan.FromTicks(durationTicks));
            }
        }
    }
}
