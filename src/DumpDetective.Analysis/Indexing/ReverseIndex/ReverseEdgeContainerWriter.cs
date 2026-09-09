using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;

using DumpDetective.Platform.Storage.Columns;
using DumpDetective.Platform.Storage.Container;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Phase C: writes the CSR <see cref="ReverseEdgeCsrBuilder"/> built (docs/cache/cache-format-clean-slate-redesign.md
/// §2) into the <c>ReverseEdgeOffsets</c>/<c>ReverseEdgeChildren</c> sections of an open
/// <see cref="CacheContainerWriter"/>. Unlike the retired per-bucket-blob format, both arrays are
/// already fully built in memory by the time this runs, so this is a plain buffered write with an
/// inline checksum, the same shape <see cref="Dominator.DominatorTreeIndexWriter"/> uses — no merge,
/// no per-bucket byte-range bookkeeping, no separate metadata section (a reader recovers the row
/// count from <c>ReverseEdgeOffsets</c>' own <c>RecordCount</c> in the TOC).
/// </summary>
internal static class ReverseEdgeContainerWriter
{
    private const int WriteBufferSize = 64 * 1024;

    public static void Write(
        CacheContainerWriter containerWriter,
        ReverseEdgeCsrResult csr,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var stopwatch = Stopwatch.StartNew();

        // Degrees + periodic checkpoints rather than the full int32[R+1] offset column: the offsets
        // are monotone with a mean step of 2.35, so full width spent 4 bytes a row on a number that
        // nearly always fits in one (docs/cache/cache-ideal-design.md §3.2, O3).
        WriteRowDirectory(containerWriter, csr.Offsets);

        progress?.Report(new AnalyzerProgressReport(0, "writing reverse-index CSR into cache.bin",
            Detail: $"row directory written ({Math.Max(0, csr.Offsets.Length - 1):N0} rows)", Elapsed: stopwatch.Elapsed));

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeChildren);
        uint childrenChecksum = WriteInt32Column(containerWriter.Stream, csr.Children);
        containerWriter.EndSection(csr.Children.Length, childrenChecksum);

        progress?.Report(new AnalyzerProgressReport(0, "writing reverse-index CSR into cache.bin",
            Detail: $"children written ({csr.Children.Length:N0} entries)", Elapsed: stopwatch.Elapsed));
    }

    /// <summary>
    /// Splits the CSR's <c>int32[R+1]</c> offset array into the three sections that replace it:
    /// one degree byte per row, an absolute checkpoint every
    /// <see cref="ReverseEdgeDegreeColumn.CheckpointStride"/> rows, and an escape table for hub rows
    /// whose in-degree does not fit a byte.
    /// </summary>
    private static void WriteRowDirectory(CacheContainerWriter containerWriter, int[] offsets)
    {
        int rowCount = Math.Max(0, offsets.Length - 1);
        var overflow = new List<(uint RecordIndex, ulong Value)>();
        int checkpointCount = ReverseEdgeDegreeColumn.CheckpointCountFor(rowCount);
        var checkpoints = new int[checkpointCount];

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeDegrees);
        var degreeHasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);
        try
        {
            int filled = 0;
            for (int row = 0; row < rowCount; row++)
            {
                if (row % ReverseEdgeDegreeColumn.CheckpointStride == 0)
                    checkpoints[row / ReverseEdgeDegreeColumn.CheckpointStride] = offsets[row];

                int degree = offsets[row + 1] - offsets[row];
                if (degree >= ReverseEdgeDegreeColumn.EscapeSentinel)
                {
                    overflow.Add(((uint)row, (ulong)degree));
                    buffer[filled++] = ReverseEdgeDegreeColumn.EscapeSentinel;
                }
                else
                {
                    buffer[filled++] = (byte)degree;
                }

                if (filled == buffer.Length)
                {
                    containerWriter.Stream.Write(buffer, 0, filled);
                    degreeHasher.Append(buffer.AsSpan(0, filled));
                    filled = 0;
                }
            }

            if (filled > 0)
            {
                containerWriter.Stream.Write(buffer, 0, filled);
                degreeHasher.Append(buffer.AsSpan(0, filled));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        containerWriter.EndSection(rowCount, degreeHasher.GetCurrentHashAsUInt32());

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeDegreeCheckpoints);
        uint checkpointChecksum = WriteInt32Column(containerWriter.Stream, checkpoints);
        containerWriter.EndSection(checkpointCount, checkpointChecksum);

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeDegreeOverflow);
        uint overflowChecksum = ColumnOverflowTable.Write(containerWriter.Stream, overflow, WriteBufferSize);
        containerWriter.EndSection(overflow.Count, overflowChecksum);
    }

    private static uint WriteInt32Column(Stream stream, int[] values)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(WriteBufferSize);

        try
        {
            int perChunk = buffer.Length / sizeof(int);
            for (int start = 0; start < values.Length; start += perChunk)
            {
                int count = Math.Min(perChunk, values.Length - start);
                for (int i = 0; i < count; i++)
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(i * sizeof(int)), values[start + i]);

                int bytes = count * sizeof(int);
                stream.Write(buffer, 0, bytes);
                hasher.Append(buffer.AsSpan(0, bytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }
}
