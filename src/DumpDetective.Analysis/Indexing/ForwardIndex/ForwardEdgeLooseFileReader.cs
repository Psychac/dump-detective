using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using DumpDetective.Analysis.Traversal.Dominator;

namespace DumpDetective.Analysis.Indexing.ForwardIndex;

/// <summary>
/// Point-lookup reader over <see cref="ForwardEdgeSorter"/>'s per-bucket <c>.dat</c>/<c>.idx</c>
/// scratch files (Phase B output), read before <see cref="ForwardEdgeContainerWriter"/> (Phase C)
/// merges them into <c>cache.bin</c> and deletes them. Lets Stage A's reachability walk
/// (<see cref="Traversal.Dominator.ReachableGraphWalker"/>, <c>buildCsr: false</c>) reuse the forward edges ClrMD already resolved
/// during the main heap scan, instead of a second per-node <c>obj.EnumerateReferences</c> walk —
/// see docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §2/§8.8.
///
/// Two prior versions of this class were measured and rejected (§8.8):
/// - v1: <c>FileStream.Seek</c>+<c>Read</c> per binary-search probe. ~7.5x slower than a live
///   ClrMD walk — hundreds of millions of raw seek/read syscalls across a multi-million-node walk.
/// - v2: memory-mapped the directory and did the binary search via pointer dereferences into the
///   mapped view (mirroring <see cref="ForwardEdgeIndexReader"/> exactly). Fixed the syscall
///   storm, but was still ~15% slower than live ClrMD — mmap makes random access *possible*
///   without a syscall, but each first touch of a directory page is still a page fault, and a
///   million-entry directory's binary search touches many distinct, effectively random pages.
///
/// **v3 (this version): decode each bucket's small <c>.idx</c> directory into plain managed
/// arrays once, up front, with one sequential read** — then binary-search those arrays (plain
/// `ulong[]`/`long[]`, `Array.BinarySearch`) instead of the mapped view. A directory entry is 16
/// bytes; even a dump with tens of millions of reachable objects has a directory total in the
/// hundreds of MB, comparable to structures this codebase already accepts in memory during the
/// walk (e.g. <c>ReachableGraphWalker</c>'s own visited-node `HashSet&lt;ulong&gt;` in
/// <c>buildCsr: false</c> mode) — and
/// unlike the mmap'd version, once decoded these arrays are fully resident, contiguous, and
/// cache-friendly for repeated binary search; no page faults, no syscalls, ever again after the
/// one-time sequential decode. The <c>.dat</c> file (the actual, larger children data) stays
/// memory-mapped — it's touched only once per lookup (the matched group), not log(n) times, so
/// mmap's lazy-fault-in-just-what's-touched property is the right tool there.
/// </summary>
internal sealed unsafe class ForwardEdgeLooseFileReader : IDisposable
{
    private const int DirectoryHeaderSize = 24; // Magic(4) + Version(4) + EntryCount(8) + Reserved(8)
    private const int GroupHeaderSize = 12;     // parent(8) + count(4)

    /// <summary>
    /// One decoded <c>.idx</c> directory entry. Layout matches the on-disk
    /// <c>[ParentAddr:8][Offset:8]</c> record exactly (sequential, no padding — both fields are
    /// 8-byte-aligned already), so a whole bucket's directory can be read straight into an array
    /// of these with zero per-entry parsing.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DirectoryEntry
    {
        public readonly ulong Parent;
        public readonly long Offset;
    }

    private readonly MemoryMappedFile?[] _dataFiles;
    private readonly MemoryMappedViewAccessor?[] _dataAccessors;
    private readonly byte*[] _dataPtrs;
    // Per bucket: sorted directory entries, decoded once from the .idx file at TryOpen time —
    // see the class doc comment for why.
    private readonly DirectoryEntry[][] _directories;
    private readonly int _bucketCount;
    private bool _disposed;

    private ForwardEdgeLooseFileReader(
        MemoryMappedFile?[] dataFiles,
        MemoryMappedViewAccessor?[] dataAccessors,
        DirectoryEntry[][] directories)
    {
        _dataFiles = dataFiles;
        _dataAccessors = dataAccessors;
        _directories = directories;
        _bucketCount = directories.Length;

        _dataPtrs = new byte*[_bucketCount];
        for (int i = 0; i < _bucketCount; i++)
        {
            MemoryMappedViewAccessor? dataAccessor = dataAccessors[i];
            if (dataAccessor is null)
                continue;

            byte* p = null;
            dataAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _dataPtrs[i] = p + dataAccessor.PointerOffset;
        }
    }

    /// <summary>
    /// Opens every bucket's loose <c>.dat</c>/<c>.idx</c> files, decoding each directory into
    /// memory. Returns <c>false</c> — never throws — if any file is missing or a directory header
    /// fails validation; callers should fall back to a different successors source in that case,
    /// same as any other optional satellite index in this codebase.
    /// </summary>
    public static bool TryOpen(string cacheDir, int bucketCount, out ForwardEdgeLooseFileReader? reader)
    {
        reader = null;
        if (bucketCount <= 0)
            return false;

        var dataFiles = new MemoryMappedFile?[bucketCount];
        var dataAccessors = new MemoryMappedViewAccessor?[bucketCount];
        var directories = new DirectoryEntry[bucketCount][];

        try
        {
            for (int i = 0; i < bucketCount; i++)
            {
                string dataFile = Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.SortedDataSuffix}");
                string dirFile = Path.Combine(cacheDir, $"forward_edges_bucket_{i}{ForwardIndexConstants.DirectorySuffix}");

                // A bucket that recorded no edges never wrote its files — zero entries, not a
                // failure (mirrors ReverseEdgeIndexReader/ForwardEdgeIndexReader's empty-bucket
                // handling for the merged-container case).
                if (!File.Exists(dataFile) || !File.Exists(dirFile))
                {
                    directories[i] = Array.Empty<DirectoryEntry>();
                    continue;
                }

                if (!TryDecodeDirectory(dirFile, out DirectoryEntry[] entries))
                {
                    CleanupAll(dataAccessors, dataFiles);
                    return false;
                }

                directories[i] = entries;

                // A directory with zero entries still writes its 24-byte header, but the sorter
                // never writes any group bytes for an empty bucket — its .dat file is 0 bytes,
                // and MemoryMappedFile.CreateFromFile throws on a truly empty file.
                if (entries.Length == 0)
                    continue;

                var dataMmf = MemoryMappedFile.CreateFromFile(dataFile, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
                dataFiles[i] = dataMmf;
                dataAccessors[i] = dataMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            }
        }
        catch
        {
            CleanupAll(dataAccessors, dataFiles);
            return false;
        }

        reader = new ForwardEdgeLooseFileReader(dataFiles, dataAccessors, directories);
        return true;
    }

    /// <summary>
    /// Sequentially reads <paramref name="dirFile"/> in full, straight into a
    /// <see cref="DirectoryEntry"/> array — one I/O pass, no per-entry parsing (the file's
    /// on-disk layout already matches the struct's memory layout byte-for-byte), and the
    /// directory is never touched again after this call.
    /// </summary>
    private static bool TryDecodeDirectory(string dirFile, out DirectoryEntry[] entries)
    {
        entries = Array.Empty<DirectoryEntry>();

        using var fs = new FileStream(dirFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);

        Span<byte> header = stackalloc byte[DirectoryHeaderSize];
        if (fs.Read(header) != DirectoryHeaderSize)
            return false;

        uint magic = BitConverter.ToUInt32(header);
        uint version = BitConverter.ToUInt32(header[4..]);
        long entryCount = BitConverter.ToInt64(header[8..]);

        if (magic != ForwardIndexConstants.Magic || version != ForwardIndexConstants.DirectoryVersion || entryCount < 0)
            return false;

        if (entryCount == 0)
            return true;

        entries = new DirectoryEntry[entryCount];
        Span<byte> entryBytes = MemoryMarshal.AsBytes(entries.AsSpan());

        // Bulk-read the whole entry table in one shot rather than one Read() per 16-byte entry —
        // a multi-million-entry directory is easily hundreds of MB, and a single large sequential
        // read is exactly what FileOptions.SequentialScan + a big buffer favors.
        int totalRead = 0;
        while (totalRead < entryBytes.Length)
        {
            int read = fs.Read(entryBytes[totalRead..]);
            if (read == 0)
                return false; // truncated file
            totalRead += read;
        }

        return true;
    }

    private static void CleanupAll(MemoryMappedViewAccessor?[] dataAccessors, MemoryMappedFile?[] dataFiles)
    {
        foreach (MemoryMappedViewAccessor? a in dataAccessors) a?.Dispose();
        foreach (MemoryMappedFile? f in dataFiles) f?.Dispose();
    }

    /// <summary>
    /// Matches <see cref="SuccessorsFunc"/>'s signature exactly, so an instance's method group can
    /// be passed straight into <see cref="Traversal.Dominator.ReachableGraphWalker.Walk"/>.
    /// </summary>
    public int GetChildren(ulong parent, ref ulong[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int bucketIdx = (int)ForwardIndexConstants.ParentBucketHash(parent, _bucketCount);
        DirectoryEntry[] entries = _directories[bucketIdx];
        if (!TryFindInDirectory(entries, parent, out long dataOffset))
            return 0;

        byte* dataPtr = _dataPtrs[bucketIdx];
        int count = ReadInt32(dataPtr, dataOffset + 8);
        if (count == 0)
            return 0;

        if (buffer.Length < count)
            buffer = new ulong[count];

        long childrenStart = dataOffset + GroupHeaderSize;
        for (int i = 0; i < count; i++)
            buffer[i] = ReadUInt64(dataPtr, childrenStart + i * sizeof(ulong));

        return count;
    }

    private static bool TryFindInDirectory(DirectoryEntry[] entries, ulong parent, out long dataOffset)
    {
        dataOffset = -1;

        int lo = 0, hi = entries.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            ulong midParent = entries[mid].Parent;

            if (midParent == parent)
            {
                dataOffset = entries[mid].Offset;
                return true;
            }

            if (midParent < parent)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return false;
    }

    private static ulong ReadUInt64(byte* basePtr, long offset) => Unsafe.ReadUnaligned<ulong>(basePtr + offset);
    private static int ReadInt32(byte* basePtr, long offset) => Unsafe.ReadUnaligned<int>(basePtr + offset);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (int i = 0; i < _bucketCount; i++)
            _dataAccessors[i]?.SafeMemoryMappedViewHandle.ReleasePointer();

        CleanupAll(_dataAccessors, _dataFiles);
    }
}
