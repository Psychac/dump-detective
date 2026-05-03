using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.Satellite;

/// <summary>
/// Writes <c>RootIndex.bin</c> — a snapshot of all GC roots enumerated from the heap.
/// </summary>
/// <remarks>
/// Record layout (20 bytes, little-endian):
///   TargetAddress (8) | RootAddress (8) | Kind (1) | Pad (3)
/// Consumers: GCRootAnalyzer, StaticRootLeakDetector, FinalizableObjectAnalyzer
/// Typical size: ~2 MB
/// </remarks>
internal static class RootIndexWriter
{
    // File magic: "RTIX" = Root Index
    private const int Magic      = 0x58495452;
    private const int Version    = 1;
    private const int RecordSize = 20; // 8 + 8 + 1 + 3 pad
    private const int ProgressEveryRoots = 10_000;

    public static long Write(
        string filePath,
        ClrHeap heap,
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
        var reporterStopwatch = stopwatch ?? Stopwatch.StartNew();
        long lastReportMs = 0;

        try
        {
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                cancellationToken.ThrowIfCancellationRequested();

                ulong targetAddr = root.Object;
                ulong rootAddr   = root.Address;
                byte  kind       = (byte)root.RootKind;

                var span = buf.AsSpan(offset, RecordSize);
                BinaryPrimitives.WriteUInt64LittleEndian(span,       targetAddr);
                BinaryPrimitives.WriteUInt64LittleEndian(span[8..],  rootAddr);
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

        stream.Flush();
        IndexHeader.PatchRecordCount(stream, recordCount);
        return recordCount;
    }
}
