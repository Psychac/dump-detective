using System.Buffers.Binary;
using System.Buffers;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using System.IO;
using System;

namespace DumpDetective.Analysis.Indexing.Satellite;

internal sealed class DiskHandleSnapshotReader : IHandleSnapshotReader
{
    // File magic: "HDSS" = Handle Snapshot — must match HandleSnapshotWriter.
    private const int Magic = 0x53534448;
    private const int RecordSizeV1 = 20; // pre-P3-3: no DependentTarget
    private const int RecordSizeV2 = 28; // P3-3: + DependentTarget (8 bytes)

    private readonly Stream _stream;
    private readonly long _recordCount;
    private readonly int _recordSize;
    private readonly bool _hasDependentTarget;

    public DiskHandleSnapshotReader(string containerPath)
    {
        if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            throw new InvalidDataException("Handle snapshot cache container invalid or missing");

        if (!reader.TryOpenSection(CacheSectionId.Handles, out Stream? sectionStream) || sectionStream is null)
            throw new InvalidDataException("Handle snapshot section not found in container");

        _stream = sectionStream;
        if (!IndexHeader.TryRead(_stream, out IndexHeader header))
            throw new InvalidDataException("Handle snapshot header invalid");

        if (header.Magic != Magic || (header.Version != 1 && header.Version != 2))
            throw new InvalidDataException($"Handle snapshot header has unsupported magic/version ({header.Magic:X8}/{header.Version})");

        _recordCount = header.RecordCount;
        _hasDependentTarget = header.Version >= 2;
        _recordSize = _hasDependentTarget ? RecordSizeV2 : RecordSizeV1;
    }

    public long? RecordCount => _recordCount;

    public IEnumerable<HandleRecord> EnumerateRecords(System.Threading.CancellationToken token)
    {
        byte[] buf = new byte[_recordSize];
        for (long i = 0; i < _recordCount; i++)
        {
            token.ThrowIfCancellationRequested();
            int read = 0;
            while (read < _recordSize)
            {
                int r = _stream.Read(buf, read, _recordSize - read);
                if (r == 0) break;
                read += r;
            }
            if (read < _recordSize) yield break;

            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(0, 8));
            ulong mt = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8));
            byte kind = buf[16];
            ulong dependentTarget = _hasDependentTarget
                ? BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(20, 8))
                : 0;
            yield return new HandleRecord(addr, mt, kind, DependentTarget: dependentTarget);
        }
    }

    public void Dispose() => _stream.Dispose();
}
