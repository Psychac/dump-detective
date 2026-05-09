using System.Buffers;
using System.Buffers.Binary;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>EventCandidateIndex.bin</c> from delegate/event-flagged objects collected
/// during Phase 1 heap scan.
/// </summary>
/// <remarks>
/// Record layout (16 bytes, little-endian):
///   Address (8) | MT (8)
/// Consumers: EventLeakAnalyzer
/// Typical size: ~8 MB
/// </remarks>
internal sealed class EventCandidateIndexWriter : IDisposable
{
    // File magic: "EVIX" = Event Index
    private const int Magic = 0x58495645;
    private const int Version = 1;
    private const int RecordSize = 16; // 8 + 8

    private readonly FileStream _stream;
    private readonly byte[] _buf;
    private int _offset;
    private long _recordCount;
    private bool _disposed;

    public EventCandidateIndexWriter(string filePath)
    {
        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write,
            FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);
        _buf = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);

        new IndexHeader(Magic, Version, recordCount: 0).WriteTo(_stream);
    }

    public void Add(ulong address, ulong methodTable)
    {
        var span = _buf.AsSpan(_offset, RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span, address);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], methodTable);

        _offset += RecordSize;
        _recordCount++;

        if (_offset + RecordSize > _buf.Length)
            FlushBuffer();
    }

    public void Flush()
    {
        FlushBuffer();
        _stream.Flush();
        IndexHeader.PatchRecordCount(_stream, _recordCount);
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
        _stream.Dispose();
    }
}
