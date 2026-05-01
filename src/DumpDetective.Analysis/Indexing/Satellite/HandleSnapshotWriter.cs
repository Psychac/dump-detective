using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>HandleSnapshot.bin</c> — a snapshot of all GC handles enumerated from the runtime.
/// </summary>
/// <remarks>
/// Record layout (20 bytes, little-endian):
///   ObjectAddress (8) | MT (8) | Kind (1) | Pad (3)
/// Consumers: GCHandleAnalyzer, WeakReferenceAnalyzer
/// Typical size: ~1 MB
/// </remarks>
internal static class HandleSnapshotWriter
{
    // File magic: "HDSS" = Handle Snapshot
    private const int Magic   = 0x53534448;
    private const int Version = 1;
    private const int RecordSize = 20; // 8 + 8 + 1 + 3 pad
    private const int ProgressEveryHandles = 25_000;

    public static long Write(
        string filePath,
        ClrRuntime runtime,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        Stopwatch? stopwatch = null)
    {
        using FileStream stream = new(filePath, FileMode.Create, FileAccess.Write,
            FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

        var header = new IndexHeader(Magic, Version, recordCount: 0);
        header.WriteTo(stream);

        byte[] buf = ArrayPool<byte>.Shared.Rent(RecordSize * 4096);
        long recordCount = 0;
        int offset = 0;

        try
        {
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClrObject target = handle.Object;
                ulong objAddr = target.Address;
                ulong mt      = target.Type?.MethodTable ?? 0;
                byte  kind    = (byte)handle.HandleKind;

                var span = buf.AsSpan(offset, RecordSize);
                BinaryPrimitives.WriteUInt64LittleEndian(span,       objAddr);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..],  mt);
                span[16] = kind;
                // bytes 17–19 left as 0 (padding — already zeroed by Rent or previous flush)
                span[17] = 0; span[18] = 0; span[19] = 0;

                offset += RecordSize;
                recordCount++;

                if (offset + RecordSize > buf.Length)
                {
                    stream.Write(buf, 0, offset);
                    offset = 0;
                }

                if (progress is not null && recordCount % ProgressEveryHandles == 0)
                    progress.Report(new(recordCount, "enumerating GC handles",
                        Detail: $"{recordCount:N0} handles", Elapsed: stopwatch?.Elapsed));
            }

            if (offset > 0)
                stream.Write(buf, 0, offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        stream.Flush();
        IndexHeader.PatchRecordCount(stream, recordCount);
        return recordCount;
    }
}
