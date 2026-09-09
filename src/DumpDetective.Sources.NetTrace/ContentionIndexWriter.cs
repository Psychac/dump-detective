using System.Buffers.Binary;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Writes the <c>trace.contention</c> section body (see <c>CacheSectionId.TraceContention</c> in
/// DumpDetective.Platform). Callers accumulate records via <see cref="Add"/> then call
/// <see cref="Flush"/> once.
/// </summary>
/// <remarks>
/// Fixed 21-byte records, little-endian: StartTicks(8) | DurationTicks(8) | ThreadId(4) | Flags(1)
/// </remarks>
internal sealed class ContentionIndexWriter : IDisposable
{
    private const int RecordSize = 8 + 8 + 4 + 1;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[RecordSize];
    private long _recordCount;
    private bool _disposed;

    public ContentionIndexWriter(Stream stream)
    {
        _stream = stream;
    }

    public void Add(long startTicks, long durationTicks, int threadId, byte flags)
    {
        Span<byte> span = _buf;
        int offset = 0;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], startTicks); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], durationTicks); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], threadId); offset += 4;
        span[offset] = flags; offset += 1;

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
