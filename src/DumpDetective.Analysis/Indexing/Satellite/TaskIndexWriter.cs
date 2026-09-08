using System.Buffers;
using System.Buffers.Binary;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>TaskIndex.bin</c> from task-flagged objects collected during Phase 1 heap scan.
/// Callers accumulate records via <see cref="Add"/> then call <see cref="Flush"/> once.
/// </summary>
/// <remarks>
/// Record layout (20 bytes, little-endian):
///   Address (8) | MT (8) | StateFlags (4)
/// StateFlags is the raw <c>m_stateFlags</c> int field if readable, otherwise 0.
/// Consumers: AsyncTaskAnalyzer
/// Typical size: ~20 MB worst-case (1M tasks)
/// </remarks>
internal sealed class TaskIndexWriter : IDisposable
{
    // File magic: "TKIX" = Task Index
    private const int Magic = 0x58494B54;
    private const int Version = 1;
    private const int RecordSize = 20; // 8 + 8 + 4

    private readonly Stream _stream;
    private readonly long _baseOffset;
    private readonly byte[] _buf;
    private int _offset;
    private long _recordCount;
    private bool _disposed;

    public TaskIndexWriter(Stream stream)
    {
        _stream = stream;
        _baseOffset = stream.Position;
        _buf = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);

        new IndexHeader(Magic, Version, recordCount: 0).WriteTo(_stream);
    }

    public void Add(ulong address, ulong methodTable, int stateFlags)
    {
        var span = _buf.AsSpan(_offset, RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span, address);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], methodTable);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], stateFlags);

        _offset += RecordSize;
        _recordCount++;

        if (_offset + RecordSize > _buf.Length)
            FlushBuffer();
    }

    public void Flush()
    {
        FlushBuffer();
        _stream.Flush();
        IndexHeader.PatchRecordCount(_stream, _recordCount, _baseOffset);
    }

    private void FlushBuffer()
    {
        if (_offset > 0)
        {
            _stream.Write(_buf, 0, _offset);
            _offset = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buf);
    }
}
