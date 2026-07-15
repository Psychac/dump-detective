using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>LohFreeBlockIndex.bin</c> — free block gaps inside LOH segments.
/// Run once after the main heap scan has completed (reads segment metadata only).
/// </summary>
/// <remarks>
/// Record layout (24 bytes, little-endian):
///   SegmentAddress (8) | Offset (8) | Size (8)
/// Consumers: LohFragmentationAnalyzer
/// Typical size: &lt; 1 MB
/// </remarks>
internal static class LohFreeBlockWriter
{
    // File magic: "LFBX" = Loh Free Block indeX
    private const int Magic = 0x5842464C;
    private const int Version = 1;
    private const int RecordSize = 24; // 8 + 8 + 8

    // "Free" objects in CLR are represented as instances of the internal Free type.
    // Their type name is "Free" with no namespace.
    private const string FreeTypeName = "Free";

    public static long Write(Stream stream, ClrHeap heap, CancellationToken cancellationToken)
    {
        long baseOffset = stream.Position;
        new IndexHeader(Magic, Version, recordCount: 0).WriteTo(stream);

        byte[] buf = ArrayPool<byte>.Shared.Rent(RecordSize * 1024);
        long recordCount = 0;
        int offset = 0;

        try
        {
            foreach (ClrSegment segment in heap.Segments)
            {
                // Only scan LOH and POH segments for free gaps.
                if (segment.Kind != GCSegmentKind.Large &&
                    segment.Kind != GCSegmentKind.Pinned)
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                ulong segAddr = segment.Start;

                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type is null)
                        continue;

                    if (!string.Equals(obj.Type.Name, FreeTypeName, StringComparison.Ordinal))
                        continue;

                    ulong freeOffset = obj.Address - segAddr;
                    ulong freeSize = obj.Size;

                    var span = buf.AsSpan(offset, RecordSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, segAddr);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[8..], freeOffset);
                    BinaryPrimitives.WriteUInt64LittleEndian(span[16..], freeSize);

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
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        stream.Flush();
        IndexHeader.PatchRecordCount(stream, recordCount, baseOffset);
        return recordCount;
    }

    /// <summary>
    /// Writes <c>LohFreeBlockIndex.bin</c> from candidates already collected during the
    /// main heap scan, avoiding a second walk of LOH/POH segments.
    /// Each candidate is <c>(SegmentStart, FreeObjectOffset, FreeObjectSize)</c>.
    /// </summary>
    public static long WriteFromCandidates(
        Stream stream,
        IEnumerable<(ulong SegStart, ulong Offset, ulong Size)> candidates,
        CancellationToken cancellationToken)
    {
        long baseOffset = stream.Position;
        new IndexHeader(Magic, Version, recordCount: 0).WriteTo(stream);

        byte[] buf = ArrayPool<byte>.Shared.Rent(RecordSize * 1024);
        long recordCount = 0;
        int offset = 0;

        try
        {
            foreach ((ulong segAddr, ulong freeOffset, ulong freeSize) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var span = buf.AsSpan(offset, RecordSize);
                BinaryPrimitives.WriteUInt64LittleEndian(span, segAddr);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], freeOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(span[16..], freeSize);

                offset += RecordSize;
                recordCount++;

                if (offset + RecordSize > buf.Length)
                {
                    stream.Write(buf, 0, offset);
                    offset = 0;
                }
            }

            if (offset > 0) stream.Write(buf, 0, offset);
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
