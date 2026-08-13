using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>RootIndex.bin</c> — a snapshot of all GC roots enumerated from the heap, plus a
/// static/thread-static field-name trailer (v2).
/// </summary>
/// <remarks>
/// Fixed record layout (20 bytes, little-endian), one per enumerated root:
///   TargetAddress (8) | RootAddress (8) | Kind (1) | Pad (3)
/// Followed by a variable-length trailer, one record per static/thread-static root that resolved
/// to a declaring field (record count stashed in the shared <see cref="IndexHeader"/>'s Reserved
/// field, see <see cref="IndexHeader.PatchReserved"/>):
///   RootAddress (8) | OwnerTypeLen (2) | FieldNameLen (2) | AppDomainId (4) | OwnerType (N) | FieldName (M)
/// Consumers: GCRootAnalyzer, StaticRootLeakDetector, FinalizableObjectAnalyzer
/// Typical size: ~2 MB (fixed records) + a few hundred KB (trailer)
/// See docs/analysis/root-field-name-index-plan.md for the design.
/// </remarks>
internal static class RootIndexWriter
{
    // File magic: "RTIX" = Root Index
    private const int Magic = 0x58495452;
    private const int Version = 2;
    private const int RecordSize = 20; // 8 + 8 + 1 + 3 pad
    private const int ProgressEveryRoots = 10_000;
    private const byte ThreadStaticVarKind = 9;
    private const byte StaticVarKind = 10;

    public static long Write(
        Stream stream,
        ClrHeap heap,
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
        var reporterStopwatch = stopwatch ?? Stopwatch.StartNew();
        long lastReportMs = 0;
        var staticRootAddresses = new HashSet<ulong>();

        try
        {
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ulong targetAddr = root.Object;
                ulong rootAddr = root.Address;
                byte kind = (byte)root.RootKind;

                if (kind is ThreadStaticVarKind or StaticVarKind && rootAddr != 0)
                    staticRootAddresses.Add(rootAddr);

                var span = buf.AsSpan(offset, RecordSize);
                BinaryPrimitives.WriteUInt64LittleEndian(span, targetAddr);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..], rootAddr);
                span[16] = kind;
                span[17] = 0; span[18] = 0; span[19] = 0;

                offset += RecordSize;
                recordCount++;

                if (offset + RecordSize > buf.Length)
                {
                    stream.Write(buf, 0, offset);
                    offset = 0;
                }

                // Report either every N roots or at least every 2 seconds so long-running
                // enumerations still show live counts even if root volume is low.
                if (progress is not null)
                {
                    long nowMs = reporterStopwatch.ElapsedMilliseconds;
                    if (recordCount % ProgressEveryRoots == 0 || nowMs - lastReportMs >= 2000)
                    {
                        progress.Report(new(recordCount, "enumerating GC roots",
                            Detail: $"{recordCount:N0} roots", Elapsed: reporterStopwatch.Elapsed));
                        lastReportMs = nowMs;
                    }
                }
            }

            if (offset > 0)
                stream.Write(buf, 0, offset);

            // Final progress snapshot so small dumps (below the reporting threshold)
            // still display a completed root count in the UI.
            if (progress is not null)
                progress.Report(new(recordCount, "enumerating GC roots", Detail: $"{recordCount:N0} roots", Elapsed: reporterStopwatch.Elapsed));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        long trailerCount = WriteFieldNameTrailer(stream, heap, staticRootAddresses, cancellationToken);

        stream.Flush();
        IndexHeader.PatchRecordCount(stream, recordCount, baseOffset);
        IndexHeader.PatchReserved(stream, trailerCount, baseOffset);
        return recordCount;
    }

    private static long WriteFieldNameTrailer(
        Stream stream,
        ClrHeap heap,
        HashSet<ulong> staticRootAddresses,
        CancellationToken cancellationToken)
    {
        if (staticRootAddresses.Count == 0)
            return 0;

        Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> fieldsByRootAddress =
            StaticFieldResolver.BuildMapByRootAddress(heap, staticRootAddresses);

        if (fieldsByRootAddress.Count == 0)
            return 0;

        byte[] buf = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int off = 0;
            long written = 0;
            foreach ((ulong rootAddr, (string typeName, string fieldName, int appDomainId)) in fieldsByRootAddress)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int typeLen = Encoding.UTF8.GetByteCount(typeName);
                int fieldLen = Encoding.UTF8.GetByteCount(fieldName);
                int recSize = 16 + typeLen + fieldLen; // RootAddr(8)+OwnerTypeLen(2)+FieldNameLen(2)+AppDomainId(4)

                if (recSize > buf.Length)
                {
                    // Rare oversized name (very long generic/nested type name) — write directly.
                    byte[] big = new byte[recSize];
                    WriteFieldRecord(big, 0, rootAddr, typeName, fieldName, appDomainId, typeLen, fieldLen);
                    stream.Write(big, 0, recSize);
                    written++;
                    continue;
                }

                if (off + recSize > buf.Length)
                {
                    stream.Write(buf, 0, off);
                    off = 0;
                }

                WriteFieldRecord(buf, off, rootAddr, typeName, fieldName, appDomainId, typeLen, fieldLen);
                off += recSize;
                written++;
            }

            if (off > 0)
                stream.Write(buf, 0, off);

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void WriteFieldRecord(byte[] buf, int off, ulong rootAddr, string typeName, string fieldName, int appDomainId, int typeLen, int fieldLen)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(off), rootAddr);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 8), (ushort)typeLen);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off + 10), (ushort)fieldLen);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(off + 12), appDomainId);
        Encoding.UTF8.GetBytes(typeName, buf.AsSpan(off + 16));
        Encoding.UTF8.GetBytes(fieldName, buf.AsSpan(off + 16 + typeLen));
    }
}
