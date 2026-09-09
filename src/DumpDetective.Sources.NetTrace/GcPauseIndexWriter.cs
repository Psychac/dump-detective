using System.Buffers.Binary;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Writes the <c>trace.gcevents</c> section body (see <c>CacheSectionId.TraceGcEvents</c> in
/// DumpDetective.Platform). Callers accumulate records via <see cref="Add"/> then call
/// <see cref="Flush"/> once.
/// </summary>
/// <remarks>
/// Fixed 34-byte records, little-endian, no length-prefixed fields (unlike
/// <c>TraceMethodIndexWriter</c> — nothing here is a string):
///   TimestampTicks(8) | ThreadId(4) | Reason(1) | PauseTicks(8) | HasGcData(1) | Generation(4) |
///   HeapBytes(8)
/// </remarks>
internal sealed class GcPauseIndexWriter : IDisposable
{
    private const int RecordSize = 8 + 4 + 1 + 8 + 1 + 4 + 8;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[RecordSize];
    private long _recordCount;
    private bool _disposed;

    public GcPauseIndexWriter(Stream stream)
    {
        _stream = stream;
    }

    public void Add(
        long timestampTicks,
        int threadId,
        byte reason,
        long pauseTicks,
        bool hasGcData,
        int generation,
        long heapBytes)
    {
        Span<byte> span = _buf;
        int offset = 0;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], timestampTicks); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], threadId); offset += 4;
        span[offset] = reason; offset += 1;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], pauseTicks); offset += 8;
        span[offset] = hasGcData ? (byte)1 : (byte)0; offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], generation); offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], heapBytes); offset += 8;

        _stream.Write(_buf, 0, offset);
        _recordCount++;
    }

    /// <summary>Flushes the underlying stream and returns the number of records written — the
    /// caller passes this to <c>CacheContainerWriter.EndSection</c>.</summary>
    public long Flush()
    {
        _stream.Flush();
        return _recordCount;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
