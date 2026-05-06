using System.Buffers.Binary;
using System.Buffers;
using DumpDetective.Analysis.Indexing;
using System.IO;
using System;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal sealed class DiskHandleSnapshotReader : IHandleSnapshotReader
{
    private readonly FileStream _stream;
    private readonly long _recordCount;
    private const int RecordSize = 20;

    public DiskHandleSnapshotReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4 * 1024, options: FileOptions.SequentialScan);
        if (!IndexHeader.TryRead(_stream, out IndexHeader header))
            throw new InvalidDataException("Handle snapshot header invalid");
        _recordCount = header.RecordCount;
    }

    public long? RecordCount => _recordCount;

    public IEnumerable<HandleRecord> EnumerateRecords(System.Threading.CancellationToken token)
    {
        byte[] buf = new byte[RecordSize];
        for (long i = 0; i < _recordCount; i++)
        {
            token.ThrowIfCancellationRequested();
            int read = 0;
            while (read < RecordSize)
            {
                int r = _stream.Read(buf, read, RecordSize - read);
                if (r == 0) break;
                read += r;
            }
            if (read < RecordSize) yield break;

            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0,8));
            ulong mt   = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8,8));
            byte kind  = buf[16];
            yield return new HandleRecord(addr, mt, kind);
        }
    }

    public void Dispose() => _stream.Dispose();
}
