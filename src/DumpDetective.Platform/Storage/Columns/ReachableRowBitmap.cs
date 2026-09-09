using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;

using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Platform.Storage.Columns;

/// <summary>
/// Which object rows the reachability walk reached, as a bitmap over object rows plus a rank
/// directory — replacing the separate sorted <c>DominatorReachableAddresses</c> column
/// (docs/cache/cache-ideal-design.md §3.1, R1).
/// </summary>
/// <remarks>
/// The reachable set is a *subset* of the object table, so persisting it as its own address column
/// stored every reachable address twice: once in <c>ObjectAddresses</c> and again here. On the
/// 27.5 GB dump that second copy was 222.99 MiB including its block bases and escape table. One bit
/// per object row is 10.38 MiB, and the rank directory that makes it indexable adds 0.32 MiB.
///
/// Two directions are needed and both are O(1):
/// <list type="bullet">
///   <item><c>rank(objectRow)</c> — how many reachable rows precede it, i.e. its reachable-row
///   index. One resident superblock read plus at most one word's <c>PopCount</c>.</item>
///   <item><c>select(reachableRow)</c> — which object row that is. Binary search of the superblock
///   ranks, then a bit walk inside one superblock.</item>
/// </list>
///
/// Addresses come from <see cref="MonotonicAddressColumn"/> over <c>ObjectAddresses</c>, which the
/// container already carries, so this holds no addresses of its own.
///
/// Layout: <c>WordCount(8) | Words(8 × WordCount) | SuperblockRanks(4 × ceil(WordCount / 8))</c>.
/// A superblock is <see cref="WordsPerSuperblock"/> words = 512 object rows, so its rank array is
/// one <c>uint32</c> per 512 rows.
/// </remarks>
internal sealed unsafe class ReachableRowBitmap : IDisposable
{
    /// <summary>Words per rank superblock. 8 × 64 bits = 512 rows, so the directory is 1/128th of the bitmap.</summary>
    public const int WordsPerSuperblock = 8;
    private const int BitsPerSuperblock = WordsPerSuperblock * 64;

    private readonly MemoryMappedViewAccessor _accessor;
    private readonly ulong* _words;
    private readonly long _wordCount;
    // Resident: one uint32 per 512 object rows — 0.32 MiB at 87.1M rows, and every rank/select
    // touches it, so mapping it would trade a trivial allocation for a page fault per query.
    private readonly uint[] _superblockRanks;
    private bool _disposed;

    /// <summary>Object rows the bitmap covers.</summary>
    public long ObjectRowCount { get; }

    /// <summary>Reachable rows — the bitmap's population count.</summary>
    public long ReachableRowCount { get; }

    private ReachableRowBitmap(
        MemoryMappedViewAccessor accessor, ulong* words, long wordCount,
        uint[] superblockRanks, long objectRowCount, long reachableRowCount)
    {
        _accessor = accessor;
        _words = words;
        _wordCount = wordCount;
        _superblockRanks = superblockRanks;
        ObjectRowCount = objectRowCount;
        ReachableRowCount = reachableRowCount;
    }

    public static long WordCountFor(long objectRowCount) => (objectRowCount + 63) / 64;

    public static int SuperblockCountFor(long wordCount) =>
        (int)((wordCount + WordsPerSuperblock - 1) / WordsPerSuperblock);

    public static bool TryOpen(CacheContainerReader container, out ReachableRowBitmap? bitmap)
    {
        bitmap = null;

        if (!container.TryGetSectionInfo(CacheSectionId.ReachableRowBitmap, out CacheTocEntry entry)
            || entry.Length < sizeof(long))
            return false;

        if (!container.TryOpenSectionAccessor(CacheSectionId.ReachableRowBitmap, out MemoryMappedViewAccessor? accessor, out _)
            || accessor is null)
        {
            accessor?.Dispose();
            return false;
        }

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        byte* body = basePtr + accessor.PointerOffset;

        long wordCount = Unsafe.ReadUnaligned<long>(body);
        int superblocks = SuperblockCountFor(wordCount);
        long expected = sizeof(long) + wordCount * sizeof(ulong) + (long)superblocks * sizeof(uint);

        if (wordCount <= 0 || entry.Length != expected)
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            return false;
        }

        var words = (ulong*)(body + sizeof(long));
        byte* rankBytes = body + sizeof(long) + wordCount * sizeof(ulong);

        var ranks = new uint[superblocks];
        for (int i = 0; i < superblocks; i++)
            ranks[i] = Unsafe.ReadUnaligned<uint>(rankBytes + (long)i * sizeof(uint));

        long population = 0;
        for (long w = 0; w < wordCount; w++)
            population += BitOperations.PopCount(words[w]);

        bitmap = new ReachableRowBitmap(accessor, words, wordCount, ranks, entry.RecordCount, population);
        return true;
    }

    /// <summary>Whether <paramref name="objectRow"/> was reached.</summary>
    public bool IsReachable(long objectRow)
    {
        if ((ulong)objectRow >= (ulong)(_wordCount * 64))
            return false;

        return (_words[objectRow >> 6] & (1UL << (int)(objectRow & 63))) != 0;
    }

    /// <summary>
    /// <paramref name="objectRow"/>'s index among reachable rows, or -1 when it is not reachable.
    /// </summary>
    public long GetReachableRow(long objectRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if ((ulong)objectRow >= (ulong)(_wordCount * 64))
            return -1;

        long word = objectRow >> 6;
        int bit = (int)(objectRow & 63);
        ulong mask = 1UL << bit;

        if ((_words[word] & mask) == 0)
            return -1;

        long superblock = word / WordsPerSuperblock;
        long rank = _superblockRanks[superblock];

        for (long w = superblock * WordsPerSuperblock; w < word; w++)
            rank += BitOperations.PopCount(_words[w]);

        // Bits strictly below this one within its own word.
        rank += BitOperations.PopCount(_words[word] & (mask - 1));
        return rank;
    }

    /// <summary>The object row holding reachable row <paramref name="reachableRow"/>.</summary>
    public long GetObjectRow(long reachableRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if ((ulong)reachableRow >= (ulong)ReachableRowCount)
            return -1;

        // Last superblock whose starting rank is at or below the target.
        int lo = 0;
        int hi = _superblockRanks.Length - 1;
        int block = 0;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_superblockRanks[mid] <= reachableRow)
            {
                block = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        long remaining = reachableRow - _superblockRanks[block];
        long firstWord = (long)block * WordsPerSuperblock;

        for (long w = firstWord; w < _wordCount; w++)
        {
            ulong value = _words[w];
            int population = BitOperations.PopCount(value);
            if (remaining >= population)
            {
                remaining -= population;
                continue;
            }

            // Clear the lowest set bit `remaining` times, then the lowest remaining one is the answer.
            for (long i = 0; i < remaining; i++)
                value &= value - 1;

            return (w << 6) + BitOperations.TrailingZeroCount(value);
        }

        return -1;
    }

    /// <summary>
    /// Visits every reachable row in ascending order as <c>(reachableRow, objectRow)</c>.
    /// </summary>
    /// <remarks>
    /// For callers that want all rows rather than one. Walking the words forward is O(objects / 64)
    /// and pairs the two row spaces as it goes; doing the same thing with
    /// <see cref="GetObjectRow"/> per row would be R selects — 58.3M of them on the 27.5 GB dump —
    /// and would turn a sequential scan into a search per element.
    /// </remarks>
    public void ForEachReachableRow(Action<long, long> onRow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long reachableRow = 0;
        for (long w = 0; w < _wordCount; w++)
        {
            ulong value = _words[w];
            while (value != 0)
            {
                int bit = BitOperations.TrailingZeroCount(value);
                onRow(reachableRow++, (w << 6) + bit);
                value &= value - 1;
            }
        }
    }

    /// <summary>
    /// Builds the section body from ascending reachable object rows and writes it, returning the
    /// checksum so the caller closes the section without a re-read pass.
    /// </summary>
    public static uint Write(Stream stream, long objectRowCount, IReadOnlyList<long> reachableObjectRows)
    {
        long wordCount = WordCountFor(objectRowCount);
        var words = new ulong[wordCount];
        foreach (long row in reachableObjectRows)
        {
            if ((ulong)row < (ulong)objectRowCount)
                words[row >> 6] |= 1UL << (int)(row & 63);
        }

        return Write(stream, words, wordCount);
    }

    /// <summary>Writes an already-built word array — the shape the walk produces directly.</summary>
    public static uint Write(Stream stream, ulong[] words, long wordCount)
    {
        int superblocks = SuperblockCountFor(wordCount);
        var ranks = new uint[superblocks];
        long running = 0;
        for (long w = 0; w < wordCount; w++)
        {
            if (w % WordsPerSuperblock == 0)
                ranks[w / WordsPerSuperblock] = (uint)running;

            running += BitOperations.PopCount(words[w]);
        }

        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            BinaryPrimitives.WriteInt64LittleEndian(buffer, wordCount);
            stream.Write(buffer, 0, sizeof(long));
            hasher.Append(buffer.AsSpan(0, sizeof(long)));

            int filled = 0;
            for (long w = 0; w < wordCount; w++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(filled), words[w]);
                filled += sizeof(ulong);
                if (filled + sizeof(ulong) > buffer.Length)
                    Flush(stream, hasher, buffer, ref filled);
            }
            if (filled > 0)
                Flush(stream, hasher, buffer, ref filled);

            for (int i = 0; i < superblocks; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(filled), ranks[i]);
                filled += sizeof(uint);
                if (filled + sizeof(uint) > buffer.Length)
                    Flush(stream, hasher, buffer, ref filled);
            }
            if (filled > 0)
                Flush(stream, hasher, buffer, ref filled);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    private static void Flush(Stream stream, XxHash32 hasher, byte[] buffer, ref int filled)
    {
        stream.Write(buffer, 0, filled);
        hasher.Append(buffer.AsSpan(0, filled));
        filled = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _accessor.Dispose();
    }
}
