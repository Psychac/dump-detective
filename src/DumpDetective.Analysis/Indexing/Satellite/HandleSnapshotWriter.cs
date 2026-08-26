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
/// Record layout (28 bytes, little-endian, v2 — P3-3):
///   ObjectAddress (8) | MT (8) | Kind (1) | Pad (3) | DependentTarget (8)
/// DependentTarget is the secondary target address for "Dependent" kind handles, resolved via
/// <see cref="DependentHandleTargetResolver"/> at write time (0 for all other kinds). Carrying it
/// in the snapshot lets <c>GCHandleAnalyzer</c> resolve dependent-handle topology in the same
/// streaming pass as every other handle kind, instead of a second live
/// <c>runtime.EnumerateHandles()</c> pass scoped to Dependent-kind handles.
/// v1 readers (RecordSize 20, no DependentTarget) remain supported — see
/// <see cref="DiskHandleSnapshotReader"/>.
/// Consumers: GCHandleAnalyzer, WeakReferenceAnalyzer
/// Typical size: ~1.4 MB
/// </remarks>
internal static class HandleSnapshotWriter
{
    // File magic: "HDSS" = Handle Snapshot
    private const int Magic = 0x53534448;
    private const int Version = 2;
    private const int RecordSize = 28; // 8 + 8 + 1 + 3 pad + 8 (DependentTarget, P3-3)
    private const int ProgressEveryHandles = 25_000;

    public static long Write(
        Stream stream,
        ClrRuntime runtime,
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

        try
        {
            foreach (ClrHandle handle in runtime.EnumerateHandles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ClrObject target = handle.Object;
                ulong objAddr = target.Address;
                ulong mt = target.Type?.MethodTable ?? 0;
                byte kind = (byte)handle.HandleKind;

                ulong dependentTarget = 0;
                if (handle.HandleKind == ClrHandleKind.Dependent)
                    DependentHandleTargetResolver.TryGetDependentTargetAddress(handle, out dependentTarget);

                var span = buf.AsSpan(offset, RecordSize);
                BinaryPrimitives.WriteUInt64LittleEndian(span, objAddr);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], mt);
                span[16] = kind;
                // bytes 17–19 left as 0 (padding — already zeroed by Rent or previous flush)
                span[17] = 0; span[18] = 0; span[19] = 0;
                BinaryPrimitives.WriteUInt64LittleEndian(span[20..], dependentTarget);

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
        IndexHeader.PatchRecordCount(stream, recordCount, baseOffset);
        return recordCount;
    }
}
