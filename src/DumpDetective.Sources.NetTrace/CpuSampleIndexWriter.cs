using System.Buffers.Binary;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Writes the <c>trace.cpu-samples</c> section body (see <c>CacheSectionId.TraceCpuSamples</c> in
/// DumpDetective.Platform). Callers accumulate records via <see cref="Add"/> then call
/// <see cref="Flush"/> once.
/// </summary>
/// <remarks>
/// Fixed 24-byte records, little-endian: TimestampTicks(8) | ProcessId(4) | ThreadId(4) |
/// InstructionPointer(8)
/// </remarks>
internal sealed class CpuSampleIndexWriter : IDisposable
{
    private const int RecordSize = 8 + 4 + 4 + 8;

    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[RecordSize];
    private long _recordCount;
    private bool _disposed;

    public CpuSampleIndexWriter(Stream stream)
    {
        _stream = stream;
    }

    public void Add(long timestampTicks, int processId, int threadId, ulong instructionPointer)
    {
        Span<byte> span = _buf;
        int offset = 0;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], timestampTicks); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], processId); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], threadId); offset += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], instructionPointer); offset += 8;

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
