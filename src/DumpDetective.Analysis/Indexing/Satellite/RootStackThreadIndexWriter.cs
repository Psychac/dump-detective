using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes the <c>RootStackThreadAttribution</c> section — §12.2
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): which thread owns each
/// Stack-kind GC root. A <c>ClrRoot</c> (what <see cref="RootIndexWriter"/>'s
/// <c>heap.EnumerateRoots()</c> pass yields) carries no thread identity, so this is a second,
/// independent pass over <see cref="ClrRuntime.Threads"/>, calling each live thread's own
/// <c>EnumerateStackRoots()</c> — cheap relative to the rest of Phase 1 (thread and per-thread
/// stack-root counts are both small, nowhere near heap-object-population scale).
/// </summary>
/// <remarks>
/// Fixed record layout (16 bytes, little-endian), one per live thread's stack root:
///   RootAddress (8) | OSThreadId (4) | ManagedThreadId (4)
/// </remarks>
internal static class RootStackThreadIndexWriter
{
    // File magic: "RTTA" = Root Thread Attribution
    private const int Magic = 0x41545452;
    private const int Version = 1;
    private const int RecordSize = 16;

    public static long Write(
        Stream stream,
        ClrHeap heap,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        Stopwatch? stopwatch = null)
    {
        long baseOffset = stream.Position;
        var header = new IndexHeader(Magic, Version, recordCount: 0);
        header.WriteTo(stream);

        byte[] buf = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);
        long recordCount = 0;
        int offset = 0;
        var reporterStopwatch = stopwatch ?? Stopwatch.StartNew();

        try
        {
            foreach (ClrThread thread in heap.Runtime.Threads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!thread.IsAlive)
                    continue;

                foreach (ClrStackRoot root in thread.EnumerateStackRoots())
                {
                    var span = buf.AsSpan(offset, RecordSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, root.Address);
                    BinaryPrimitives.WriteUInt32LittleEndian(span[8..], thread.OSThreadId);
                    BinaryPrimitives.WriteInt32LittleEndian(span[12..], thread.ManagedThreadId);

                    offset += RecordSize;
                    recordCount++;

                    if (offset + RecordSize > buf.Length)
                    {
                        stream.Write(buf, 0, offset);
                        offset = 0;
                    }
                }
            }

            if (offset > 0)
                stream.Write(buf, 0, offset);

            progress?.Report(new(recordCount, "enumerating stack root thread ownership",
                Detail: $"{recordCount:N0} stack roots", Elapsed: reporterStopwatch.Elapsed));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        stream.Flush();
        IndexHeader.PatchRecordCount(stream, recordCount, baseOffset);
        return recordCount;
    }
}
