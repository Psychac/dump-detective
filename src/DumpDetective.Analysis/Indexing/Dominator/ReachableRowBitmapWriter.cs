using System.Buffers;
using System.Buffers.Binary;

using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Turns the walk's sorted reachable-address set into the <see cref="CacheSectionId.ReachableRowBitmap"/>
/// section — one bit per object row (docs/cache/cache-ideal-design.md §3.1, R1).
/// </summary>
/// <remarks>
/// Both inputs ascend: the reachable set is sorted, and the object address column is monotonic by
/// construction since the writer sorts segments by <c>Start</c> (O1). So the address → row mapping
/// for the whole reachable set is **one sequential merge-join**, not R binary searches — O(objects +
/// reachable) with two cursors, against 58.3M rank lookups at ~66 ns each on the 27.5 GB dump.
///
/// This replaces <c>DominatorReachableAddressWriter</c>, which persisted every reachable address a
/// second time (222.99 MiB with bases and escapes at 27.5 GB, against 10.70 MiB here). The
/// addresses themselves are already in <c>ObjectAddresses</c>; only membership needed recording.
///
/// Reads the per-segment address scratch files rather than the section just written, because a
/// <see cref="CacheContainerWriter"/> is write-only mid-build. Those files hold raw <c>ulong</c>s in
/// row order — the block-delta encoding happens during concatenation, not in the scratch.
/// </remarks>
internal static class ReachableRowBitmapWriter
{
    private const int ReadBufferSize = 1 << 20;

    /// <summary>
    /// Writes the bitmap. <paramref name="sortedReachableAddresses"/> must ascend; the walk produces
    /// it that way when <c>captureSortedAddresses: true</c>.
    /// </summary>
    /// <returns>How many reachable addresses matched an object row. A shortfall is not an error —
    /// see the remark on bogus root addresses below — but the caller may want it for diagnostics.</returns>
    public static long Write(
        CacheContainerWriter containerWriter,
        string[] addressScratchFiles,
        long objectCount,
        IReadOnlyList<ulong> sortedReachableAddresses)
    {
        long wordCount = ReachableRowBitmap.WordCountFor(objectCount);
        var words = new ulong[wordCount];
        long matched = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            int reachableCursor = 0;
            long objectRow = 0;

            for (int f = 0; f < addressScratchFiles.Length && reachableCursor < sortedReachableAddresses.Count; f++)
            {
                if (!File.Exists(addressScratchFiles[f]))
                    continue;

                using var fs = new FileStream(addressScratchFiles[f], FileMode.Open, FileAccess.Read,
                    FileShare.Read, ReadBufferSize, FileOptions.SequentialScan);

                int carried = 0;
                while (true)
                {
                    int read = fs.Read(buffer, carried, buffer.Length - carried);
                    int available = carried + read;
                    if (available < sizeof(ulong))
                        break;

                    int consumed = 0;
                    while (available - consumed >= sizeof(ulong))
                    {
                        ulong address = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(consumed));
                        consumed += sizeof(ulong);

                        // Advance past reachable addresses that precede this object. They belong to
                        // no live object at all — root enumeration yields a handful of tagged or
                        // garbage pointers (4-5 of 58.3M, always outside the heap range), and they
                        // simply do not get a bit.
                        while (reachableCursor < sortedReachableAddresses.Count
                               && sortedReachableAddresses[reachableCursor] < address)
                        {
                            reachableCursor++;
                        }

                        if (reachableCursor >= sortedReachableAddresses.Count)
                            break;

                        if (sortedReachableAddresses[reachableCursor] == address)
                        {
                            words[objectRow >> 6] |= 1UL << (int)(objectRow & 63);
                            matched++;
                            reachableCursor++;
                        }

                        objectRow++;
                    }

                    carried = available - consumed;
                    if (carried > 0)
                        Array.Copy(buffer, consumed, buffer, 0, carried);

                    // read == 0 means end of file; anything still carried is a partial record the
                    // scratch writer never produces, so there is nothing more to do with it.
                    if (read == 0)
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        containerWriter.BeginSection(CacheSectionId.ReachableRowBitmap);
        uint checksum = ReachableRowBitmap.Write(containerWriter.Stream, words, wordCount);
        containerWriter.EndSection(objectCount, checksum);

        return matched;
    }
}
