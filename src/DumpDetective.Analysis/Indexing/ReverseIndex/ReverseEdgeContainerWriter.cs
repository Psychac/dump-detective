using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;

using DumpDetective.Analysis.Indexing.Container;
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

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeOffsets);
        uint offsetsChecksum = WriteInt32Column(containerWriter.Stream, csr.Offsets);
        containerWriter.EndSection(csr.Offsets.Length, offsetsChecksum);

        progress?.Report(new AnalyzerProgressReport(0, "writing reverse-index CSR into cache.bin",
            Detail: $"offsets written ({csr.Offsets.Length:N0} rows)", Elapsed: stopwatch.Elapsed));

        containerWriter.BeginSection(CacheSectionId.ReverseEdgeChildren);
        uint childrenChecksum = WriteInt32Column(containerWriter.Stream, csr.Children);
        containerWriter.EndSection(csr.Children.Length, childrenChecksum);

        progress?.Report(new AnalyzerProgressReport(0, "writing reverse-index CSR into cache.bin",
            Detail: $"children written ({csr.Children.Length:N0} entries)", Elapsed: stopwatch.Elapsed));
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
