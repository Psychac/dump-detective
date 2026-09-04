using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.ReverseIndex;

/// <summary>
/// Read-only query path over the disk-backed reverse-reference index: "who holds this object?"
/// Opens the <c>ReverseEdgeMetadata</c>/<c>ReverseEdgeBuckets</c>/<c>ReverseEdgeDirectories</c>
/// sections written by <see cref="ReverseEdgeContainerWriter"/> and answers
/// <see cref="TryGetParents"/> via a per-bucket directory binary search followed by a single seek
/// into that bucket's data slice. Holds raw pointers into the memory-mapped sections for the
/// reader's lifetime (see <see cref="Indexing.ObjectIndexReader"/> for the same idiom) to avoid
/// <see cref="MemoryMappedViewAccessor"/>'s per-call bounds-check overhead on this query path.
/// </summary>
internal sealed unsafe class ReverseEdgeIndexReader : IDisposable
{
    private const int DirectoryHeaderSize = 24;
    private const int DirectoryEntrySize = 16;
    private const int GroupHeaderSize = 16; // child(8) + count(4) + truncated(1) + pad(3)

    private readonly MemoryMappedViewAccessor _bucketsAccessor;
    private readonly MemoryMappedViewAccessor _directoriesAccessor;
    private readonly byte* _bucketsPtr;
    private readonly byte* _directoriesPtr;
    private readonly ReverseIndexBucketLocation[] _bucketLocations;
    private readonly object[] _bucketLocks;
    private readonly int _bucketCount;
    private bool _disposed;

    private ReverseEdgeIndexReader(
        MemoryMappedViewAccessor bucketsAccessor,
        MemoryMappedViewAccessor directoriesAccessor,
        ReverseIndexBucketLocation[] bucketLocations)
    {
        _bucketsAccessor = bucketsAccessor;
        _directoriesAccessor = directoriesAccessor;
        _bucketLocations = bucketLocations;
        _bucketCount = bucketLocations.Length;
        _bucketLocks = new object[_bucketCount];
        for (int i = 0; i < _bucketCount; i++)
            _bucketLocks[i] = new object();

        byte* p = null;
        _bucketsAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _bucketsPtr = p + _bucketsAccessor.PointerOffset;

        p = null;
        _directoriesAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _directoriesPtr = p + _directoriesAccessor.PointerOffset;
    }

    /// <summary>
    /// Attempts to open the reverse-index sections from <paramref name="container"/>. Returns
    /// <c>false</c> — same as a missing/corrupt satellite section elsewhere in this container —
    /// if the metadata section is absent, malformed, or the bucket data/directory sections are
    /// empty; callers fall back to on-demand forward-ref enumeration in that case.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out ReverseEdgeIndexReader? reader)
    {
        reader = null;

        if (!container.TryOpenSection(CacheSectionId.ReverseEdgeMetadata, out Stream? metaStream) || metaStream is null)
            return false;

        ReverseIndexMetadata? metadata;
        using (metaStream)
        {
            try
            {
                metadata = JsonSerializer.Deserialize<ReverseIndexMetadata>(metaStream);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (metadata is null || metadata.BucketCount <= 0 || metadata.Buckets.Count != metadata.BucketCount)
            return false;

        var bucketLocations = new ReverseIndexBucketLocation[metadata.BucketCount];
        foreach (ReverseIndexBucketLocation loc in metadata.Buckets)
        {
            if (loc.BucketIndex < 0 || loc.BucketIndex >= metadata.BucketCount)
                return false;
            bucketLocations[loc.BucketIndex] = loc;
        }

        if (!container.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeBuckets, out MemoryMappedViewAccessor? bucketsAccessor, out long bucketsLength)
            || bucketsAccessor is null || bucketsLength == 0)
        {
            return false;
        }

        if (!container.TryOpenSectionAccessor(CacheSectionId.ReverseEdgeDirectories, out MemoryMappedViewAccessor? directoriesAccessor, out long directoriesLength)
            || directoriesAccessor is null || directoriesLength == 0)
        {
            bucketsAccessor.Dispose();
            return false;
        }

        reader = new ReverseEdgeIndexReader(bucketsAccessor, directoriesAccessor, bucketLocations);
        return true;
    }

    /// <summary>
    /// Retrieves all recorded parent addresses for <paramref name="child"/>.
    /// Returns <c>false</c> if <paramref name="child"/> has no recorded parents (not present in
    /// the index); <paramref name="parents"/> is empty in that case.
    /// Uncapped since §4.2/§7.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) —
    /// <paramref name="parents"/> always holds every recorded parent, however many there are.
    /// <paramref name="truncated"/> is kept for on-disk format/caller compatibility but is always
    /// <c>false</c> now.
    /// </summary>
    // ── Measurement scaffolding: open question 1 in docs/cache/cache-redesign-measurements.md ──
    // DD_PERF_REVERSE_BLOCKS=1 records, per lookup, which 64 KB block of ReverseEdgeBuckets and
    // ReverseEdgeDirectories the lookup lands in. Post-processing that trace answers whether block
    // compression of these sections would thrash: the sections are 245.9 MB + 107.0 MB on the
    // reference dump, and bucket assignment is by *hash* of the child address, so locality is the
    // open worry (format doc § 7.2.1). Recording only — no behaviour change.
    internal static readonly bool PerfLogBlocks =
        Environment.GetEnvironmentVariable("DD_PERF_REVERSE_BLOCKS") == "1";
    private const int BlockShift = 16;                  // 64 KB blocks
    private static readonly object s_traceGate = new();
    private static readonly List<int> s_dataBlocks = new(1 << 20);
    private static readonly List<int> s_dirBlocks = new(1 << 20);
    private static long s_lookups;
    private static long s_lookupMisses;

    /// <summary>Writes the recorded block trace next to the report and returns a one-line summary.</summary>
    internal static string DumpBlockTrace(string directory)
    {
        lock (s_traceGate)
        {
            if (s_lookups == 0)
                return "[PERF] ReverseBlocks: no TryGetParents calls recorded";

            string path = Path.Combine(directory, "reverse-block-trace.bin");
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(s_dataBlocks.Count);
                foreach (int b in s_dataBlocks) bw.Write(b);
                bw.Write(s_dirBlocks.Count);
                foreach (int b in s_dirBlocks) bw.Write(b);
            }

            return $"[PERF] ReverseBlocks: {s_lookups:N0} TryGetParents calls " +
                   $"({s_lookupMisses:N0} misses), {s_dataBlocks.Count:N0} data-block touches, " +
                   $"{s_dataBlocks.Distinct().Count():N0} distinct data blocks, " +
                   $"{s_dirBlocks.Distinct().Count():N0} distinct directory blocks -> {path}";
        }
    }

    public bool TryGetParents(ulong child, out IReadOnlyList<ulong> parents, out bool truncated)
    {
        parents = Array.Empty<ulong>();
        truncated = false;

        int bucketIdx = (int)ReverseIndexConstants.ChildBucketHash(child, _bucketCount);
        ReverseIndexBucketLocation loc = _bucketLocations[bucketIdx];

        lock (_bucketLocks[bucketIdx])
        {
            if (!TryFindInDirectory(loc, child, out long dataOffsetInBucket))
            {
                if (PerfLogBlocks)
                    lock (s_traceGate) { s_lookups++; s_lookupMisses++; }
                return false;
            }

            long absoluteDataOffset = loc.DataOffset + dataOffsetInBucket;

            if (PerfLogBlocks)
            {
                lock (s_traceGate)
                {
                    s_lookups++;
                    s_dataBlocks.Add((int)(absoluteDataOffset >> BlockShift));
                    s_dirBlocks.Add((int)(loc.DirectoryOffset >> BlockShift));
                }
            }

            ReadGroup(absoluteDataOffset, out parents, out truncated);
            return true;
        }
    }

    /// <summary>
    /// Sequentially walks every bucket's directory (already sorted by child address at write
    /// time) and, for each entry, reads only the group header's count/truncated fields — never
    /// the parent-address list itself, unlike <see cref="TryGetParents"/>. No bucket-address
    /// hashing and no <see cref="_bucketLocks"/> locking: this is a single-threaded, in-order
    /// scan over already-mapped memory, so it avoids both the point-lookup contention
    /// <see cref="TryGetParents"/> would hit if called once per heap object (bucket count is
    /// sized for ~500MB/bucket during index build, not for read-time parallelism, so a caller
    /// hammering random point lookups from N parallel workers can serialize on far fewer than
    /// N buckets) and the per-call parent-array allocation.
    /// </summary>
    public void EnumerateChildCounts(Action<ulong, int, bool> onChild)
    {
        for (int b = 0; b < _bucketCount; b++)
        {
            ReverseIndexBucketLocation loc = _bucketLocations[b];
            if (loc.DirectoryLength < DirectoryHeaderSize)
                continue;

            long entryCount = ReadInt64(_directoriesPtr, loc.DirectoryOffset + 8);
            long entriesStart = loc.DirectoryOffset + DirectoryHeaderSize;

            for (long i = 0; i < entryCount; i++)
            {
                long entryOffset = entriesStart + i * DirectoryEntrySize;
                ulong child = ReadUInt64(_directoriesPtr, entryOffset);
                long dataOffsetInBucket = ReadInt64(_directoriesPtr, entryOffset + 8);
                long absoluteDataOffset = loc.DataOffset + dataOffsetInBucket;

                int count = ReadInt32(_bucketsPtr, absoluteDataOffset + 8);
                bool truncated = _bucketsPtr[absoluteDataOffset + 12] != 0;

                onChild(child, count, truncated);
            }
        }
    }

    private bool TryFindInDirectory(ReverseIndexBucketLocation loc, ulong child, out long dataOffsetInBucket)
    {
        dataOffsetInBucket = -1;

        if (loc.DirectoryLength < DirectoryHeaderSize)
            return false;

        long entryCount = ReadInt64(_directoriesPtr, loc.DirectoryOffset + 8);
        long entriesStart = loc.DirectoryOffset + DirectoryHeaderSize;

        long lo = 0, hi = entryCount - 1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            long entryOffset = entriesStart + mid * DirectoryEntrySize;

            ulong midChild = ReadUInt64(_directoriesPtr, entryOffset);
            if (midChild == child)
            {
                dataOffsetInBucket = ReadInt64(_directoriesPtr, entryOffset + 8);
                return true;
            }

            if (midChild < child)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return false;
    }

    private void ReadGroup(long absoluteDataOffset, out IReadOnlyList<ulong> parents, out bool truncated)
    {
        // childAddr at offset+0 is redundant with the directory lookup that got us here — skip it.
        int count = ReadInt32(_bucketsPtr, absoluteDataOffset + 8);
        truncated = _bucketsPtr[absoluteDataOffset + 12] != 0;

        var result = new ulong[count];
        long parentsStart = absoluteDataOffset + GroupHeaderSize;
        for (int i = 0; i < count; i++)
            result[i] = ReadUInt64(_bucketsPtr, parentsStart + i * sizeof(ulong));

        parents = result;
    }

    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);
    private static long ReadInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<long>(basePtr + offset);
    private static int ReadInt32(byte* basePtr, long offset) => Unsafe.ReadUnaligned<int>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _bucketsAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _directoriesAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        _bucketsAccessor.Dispose();
        _directoriesAccessor.Dispose();
    }
}
