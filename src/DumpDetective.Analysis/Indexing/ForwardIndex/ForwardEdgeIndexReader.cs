using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Read-only query path over the disk-backed forward-reference index: "what does this object
/// point at?" Mirrors <see cref="ReverseIndex.ReverseEdgeIndexReader"/> exactly, except the group
/// record layout is 12 bytes (not 16 — no truncated flag/padding, since this index is never
/// capped): <c>[parent:8][count:4][children:8*count]</c>.
/// </summary>
internal sealed unsafe class ForwardEdgeIndexReader : IDisposable
{
    private const int DirectoryHeaderSize = 24;
    private const int DirectoryEntrySize = 16;
    private const int GroupHeaderSize = 12; // parent(8) + count(4)

    private readonly MemoryMappedViewAccessor _bucketsAccessor;
    private readonly MemoryMappedViewAccessor _directoriesAccessor;
    private readonly byte* _bucketsPtr;
    private readonly byte* _directoriesPtr;
    private readonly ForwardIndexBucketLocation[] _bucketLocations;
    private readonly object[] _bucketLocks;
    private readonly int _bucketCount;
    private bool _disposed;

    private ForwardEdgeIndexReader(
        MemoryMappedViewAccessor bucketsAccessor,
        MemoryMappedViewAccessor directoriesAccessor,
        ForwardIndexBucketLocation[] bucketLocations)
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
    /// Attempts to open the forward-index sections from <paramref name="container"/>. Returns
    /// <c>false</c> — same as a missing/corrupt satellite section elsewhere in this container — if
    /// the metadata section is absent, malformed, or the bucket data/directory sections are empty;
    /// callers fall back to a live forward walk in that case.
    /// </summary>
    public static bool TryOpen(CacheContainerReader container, out ForwardEdgeIndexReader? reader)
    {
        reader = null;

        if (!container.TryOpenSection(CacheSectionId.ForwardEdgeMetadata, out Stream? metaStream) || metaStream is null)
            return false;

        ForwardIndexMetadata? metadata;
        using (metaStream)
        {
            try
            {
                metadata = JsonSerializer.Deserialize<ForwardIndexMetadata>(metaStream);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (metadata is null || metadata.BucketCount <= 0 || metadata.Buckets.Count != metadata.BucketCount)
            return false;

        var bucketLocations = new ForwardIndexBucketLocation[metadata.BucketCount];
        foreach (ForwardIndexBucketLocation loc in metadata.Buckets)
        {
            if (loc.BucketIndex < 0 || loc.BucketIndex >= metadata.BucketCount)
                return false;
            bucketLocations[loc.BucketIndex] = loc;
        }

        if (!container.TryOpenSectionAccessor(CacheSectionId.ForwardEdgeBuckets, out MemoryMappedViewAccessor? bucketsAccessor, out long bucketsLength)
            || bucketsAccessor is null || bucketsLength == 0)
        {
            return false;
        }

        if (!container.TryOpenSectionAccessor(CacheSectionId.ForwardEdgeDirectories, out MemoryMappedViewAccessor? directoriesAccessor, out long directoriesLength)
            || directoriesAccessor is null || directoriesLength == 0)
        {
            bucketsAccessor.Dispose();
            return false;
        }

        reader = new ForwardEdgeIndexReader(bucketsAccessor, directoriesAccessor, bucketLocations);
        return true;
    }

    /// <summary>
    /// Retrieves all recorded child addresses for <paramref name="parent"/>. Returns <c>false</c>
    /// if <paramref name="parent"/> has no recorded children (leaf object — the common case, not
    /// an error).
    /// </summary>
    public bool TryGetChildren(ulong parent, out IReadOnlyList<ulong> children)
    {
        children = Array.Empty<ulong>();

        int bucketIdx = (int)ForwardIndexConstants.ParentBucketHash(parent, _bucketCount);
        ForwardIndexBucketLocation loc = _bucketLocations[bucketIdx];

        lock (_bucketLocks[bucketIdx])
        {
            if (!TryFindInDirectory(loc, parent, out long dataOffsetInBucket))
                return false;

            ReadGroup(loc.DataOffset + dataOffsetInBucket, out children);
            return true;
        }
    }

    /// <summary>
    /// Allocation-free <see cref="TryGetChildren"/> for callers that consume the children immediately
    /// — see <see cref="Core.Abstractions.IForwardReferenceProvider.GetChildren"/>. Reuses
    /// <paramref name="buffer"/> across calls, growing it only when a parent has more children than
    /// it currently holds, so a whole-graph walk allocates a handful of buffers instead of one array
    /// per node.
    /// </summary>
    public int GetChildren(ulong parent, ref ulong[] buffer)
    {
        int bucketIdx = (int)ForwardIndexConstants.ParentBucketHash(parent, _bucketCount);
        ForwardIndexBucketLocation loc = _bucketLocations[bucketIdx];

        lock (_bucketLocks[bucketIdx])
        {
            if (!TryFindInDirectory(loc, parent, out long dataOffsetInBucket))
                return 0;

            return ReadGroupInto(loc.DataOffset + dataOffsetInBucket, ref buffer);
        }
    }

    private bool TryFindInDirectory(ForwardIndexBucketLocation loc, ulong parent, out long dataOffsetInBucket)
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

            ulong midParent = ReadUInt64(_directoriesPtr, entryOffset);
            if (midParent == parent)
            {
                dataOffsetInBucket = ReadInt64(_directoriesPtr, entryOffset + 8);
                return true;
            }

            if (midParent < parent)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return false;
    }

    private void ReadGroup(long absoluteDataOffset, out IReadOnlyList<ulong> children)
    {
        // parentAddr at offset+0 is redundant with the directory lookup that got us here — skip it.
        int count = ReadInt32(_bucketsPtr, absoluteDataOffset + 8);

        var result = new ulong[count];
        long childrenStart = absoluteDataOffset + GroupHeaderSize;
        for (int i = 0; i < count; i++)
            result[i] = ReadUInt64(_bucketsPtr, childrenStart + i * sizeof(ulong));

        children = result;
    }

    /// <summary>
    /// <see cref="ReadGroup"/> without the per-call array: copies straight from the mapped view into
    /// <paramref name="buffer"/>, resizing only when the group doesn't fit.
    /// </summary>
    private int ReadGroupInto(long absoluteDataOffset, ref ulong[] buffer)
    {
        int count = ReadInt32(_bucketsPtr, absoluteDataOffset + 8);
        if (count == 0)
            return 0;

        if (buffer.Length < count)
            buffer = new ulong[count];

        long childrenStart = absoluteDataOffset + GroupHeaderSize;
        for (int i = 0; i < count; i++)
            buffer[i] = ReadUInt64(_bucketsPtr, childrenStart + i * sizeof(ulong));

        return count;
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
