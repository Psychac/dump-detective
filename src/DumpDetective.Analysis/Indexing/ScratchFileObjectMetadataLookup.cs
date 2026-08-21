using System.IO.MemoryMappedFiles;

using DumpDetective.Analysis.Indexing.Satellite;

namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// One segment's <see cref="SegmentIndexEntry"/> paired with its per-segment
/// <c>Address</c>/<c>MethodTable</c>/<c>Size</c> scratch file paths — the per-segment scratch files
/// <c>DiskBackedObjectIndexWriter.ConcatenateScratchFiles</c> writes during the columnar object scan,
/// before they're copied into the container and (normally) deleted. Record indices within these files
/// are local (0..RecordCount-1), unlike <see cref="SegmentIndexEntry.FirstRecordIndex"/>, which is an
/// offset into the *merged* container column these files feed but were never themselves concatenated
/// into.
/// </summary>
internal readonly struct ScratchSegmentSource
{
    public readonly SegmentIndexEntry Entry;
    public readonly string AddressPath;
    public readonly string MethodTablePath;
    public readonly string SizePath;

    public ScratchSegmentSource(SegmentIndexEntry entry, string addressPath, string methodTablePath, string sizePath)
    {
        Entry = entry;
        AddressPath = addressPath;
        MethodTablePath = methodTablePath;
        SizePath = sizePath;
    }
}

/// <summary>
/// §10.1/§10.4 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): the same
/// address → (MethodTable, Size) two-level binary search <see cref="ObjectAddressLookup"/> already
/// does post-<c>Finish()</c>, but sourced from the per-segment scratch files that feed the merged
/// <c>ObjectAddresses</c>/<c>ObjectMethodTables</c>/<c>ObjectSizes</c> container sections — usable
/// *during* Phase 1's own index build, before a finalized container exists to reopen. Exists because
/// Stage B's retained-bytes rollup (§10.4) needs each reachable node's shallow size mid-build, and
/// <c>cache.TryGetObjectMetadata</c>'s disk-backed path is unusable until <c>Finish()</c> writes a
/// complete TOC — see §10.1 for the full analysis of why a live-ClrMD fallback was rejected in favor
/// of this.
///
/// Unlike <see cref="ObjectAddressLookup"/>'s single mmap over the merged column, this opens one small
/// mmap triple per segment — record indices are local to each segment's own scratch file
/// (0..RecordCount-1), not offset by <see cref="SegmentIndexEntry.FirstRecordIndex"/>, since these
/// files were never concatenated. Callers own the scratch files themselves (this class only reads
/// them) — deleting them once this lookup is disposed is the caller's responsibility, not this
/// class's, mirroring how <c>ConcatenateScratchFiles</c> already owns deletion for the merged path.
/// </summary>
internal sealed class ScratchFileObjectMetadataLookup : IDisposable
{
    private const int ColumnSize = sizeof(ulong);

    // §10.8 measurement pass (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md):
    // FindRecord's binary search assumes each segment's own scratch file is address-sorted, carried
    // over from ObjectAddressLookup's merged-column case but never confirmed on a real per-segment
    // scratch file. Set DD_PERF_DOMINATOR_STAGEB=1 to verify that assumption at open time instead of
    // trusting it silently.
    private static readonly bool VerifyMonotonicity =
        Environment.GetEnvironmentVariable("DD_PERF_DOMINATOR_STAGEB") == "1";

    private readonly struct OpenSegment
    {
        public readonly SegmentIndexEntry Entry;
        public readonly MemoryMappedFile AddressFile;
        public readonly MemoryMappedViewAccessor AddressAccessor;
        public readonly MemoryMappedFile MethodTableFile;
        public readonly MemoryMappedViewAccessor MethodTableAccessor;
        public readonly MemoryMappedFile SizeFile;
        public readonly MemoryMappedViewAccessor SizeAccessor;

        public OpenSegment(
            SegmentIndexEntry entry,
            MemoryMappedFile addressFile, MemoryMappedViewAccessor addressAccessor,
            MemoryMappedFile methodTableFile, MemoryMappedViewAccessor methodTableAccessor,
            MemoryMappedFile sizeFile, MemoryMappedViewAccessor sizeAccessor)
        {
            Entry = entry;
            AddressFile = addressFile;
            AddressAccessor = addressAccessor;
            MethodTableFile = methodTableFile;
            MethodTableAccessor = methodTableAccessor;
            SizeFile = sizeFile;
            SizeAccessor = sizeAccessor;
        }
    }

    // Sorted by Entry.Start — segment write order isn't guaranteed to be address-sorted (same
    // reasoning as ObjectAddressLookup), so this instance sorts its own copy at open time.
    private readonly OpenSegment[] _segmentsByStart;
    private bool _disposed;

    private ScratchFileObjectMetadataLookup(OpenSegment[] segmentsByStart)
    {
        _segmentsByStart = segmentsByStart;
    }

    /// <summary>
    /// Opens mmap accessors for every segment in <paramref name="segments"/>. Never throws on a
    /// per-segment failure — a segment whose scratch files can't be opened (already deleted, I/O
    /// error) is skipped, matching every other optional-satellite-index contract in this codebase;
    /// callers get a lookup that's simply blind to that segment's objects, not a total failure.
    /// Returns <c>false</c> only if <paramref name="segments"/> is empty or every segment failed.
    /// </summary>
    public static bool TryOpen(IReadOnlyList<ScratchSegmentSource> segments, out ScratchFileObjectMetadataLookup? lookup)
    {
        lookup = null;
        if (segments.Count == 0)
            return false;

        var opened = new List<OpenSegment>(segments.Count);
        foreach (ScratchSegmentSource source in segments)
        {
            if (!TryOpenSegment(source, out OpenSegment segment))
                continue;

            opened.Add(segment);
        }

        if (opened.Count == 0)
            return false;

        OpenSegment[] segmentsByStart = opened.ToArray();
        Array.Sort(segmentsByStart, static (a, b) => a.Entry.Start.CompareTo(b.Entry.Start));

        lookup = new ScratchFileObjectMetadataLookup(segmentsByStart);
        return true;
    }

    private static bool TryOpenSegment(ScratchSegmentSource source, out OpenSegment segment)
    {
        segment = default;

        MemoryMappedFile? addrFile = null, mtFile = null, sizeFile = null;
        MemoryMappedViewAccessor? addrAcc = null, mtAcc = null, sizeAcc = null;
        try
        {
            if (!File.Exists(source.AddressPath) || !File.Exists(source.MethodTablePath) || !File.Exists(source.SizePath))
                return false;

            addrFile = MemoryMappedFile.CreateFromFile(source.AddressPath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            addrAcc = addrFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            mtFile = MemoryMappedFile.CreateFromFile(source.MethodTablePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            mtAcc = mtFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            sizeFile = MemoryMappedFile.CreateFromFile(source.SizePath, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            sizeAcc = sizeFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            segment = new OpenSegment(source.Entry, addrFile, addrAcc, mtFile, mtAcc, sizeFile, sizeAcc);

            if (VerifyMonotonicity)
                VerifySegmentMonotonicity(segment);

            return true;
        }
        catch
        {
            addrAcc?.Dispose();
            addrFile?.Dispose();
            mtAcc?.Dispose();
            mtFile?.Dispose();
            sizeAcc?.Dispose();
            sizeFile?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Looks up <paramref name="address"/>. Returns <c>false</c> when the address falls in a segment
    /// this lookup couldn't open (§<c>TryOpen</c>), between segments, or doesn't land exactly on a
    /// record boundary — same "not an error" contract as <see cref="ObjectAddressLookup.TryGetEntry"/>.
    /// </summary>
    public bool TryGetEntry(ulong address, out ulong methodTable, out ulong size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        methodTable = 0;
        size = 0;

        int segIdx = FindSegment(address);
        if (segIdx < 0)
            return false;

        OpenSegment segment = _segmentsByStart[segIdx];
        long recordIndex = FindRecord(segment, address);
        if (recordIndex < 0)
            return false;

        long byteOffset = recordIndex * ColumnSize;
        methodTable = segment.MethodTableAccessor.ReadUInt64(byteOffset);
        size = segment.SizeAccessor.ReadUInt64(byteOffset);
        return true;
    }

    /// <summary>
    /// §10.8: confirms the assumption <see cref="FindRecord"/> relies on but that was never verified
    /// against a real per-segment scratch file — strictly increasing addresses within the segment.
    /// Diagnostic only (logs, doesn't throw); a violation means <see cref="FindRecord"/>'s binary
    /// search can silently miss entries for this segment, same "not an error" contract as a lookup
    /// miss, just worth knowing about before trusting the assumption elsewhere.
    /// </summary>
    private static void VerifySegmentMonotonicity(OpenSegment segment)
    {
        long recordCount = segment.Entry.RecordCount;
        ulong previous = 0;
        long violations = 0;
        for (long i = 0; i < recordCount; i++)
        {
            ulong current = segment.AddressAccessor.ReadUInt64(i * ColumnSize);
            if (i > 0 && current <= previous)
                violations++;

            previous = current;
        }

        if (violations > 0)
        {
            Console.Error.WriteLine($"[PERF] DominatorStageB: segment [0x{segment.Entry.Start:X}, 0x{segment.Entry.End:X}) " +
                $"has {violations:N0} non-increasing address transitions out of {recordCount:N0} records " +
                "— FindRecord's binary search assumption is VIOLATED for this segment");
        }
        else
        {
            Console.Error.WriteLine($"[PERF] DominatorStageB: segment [0x{segment.Entry.Start:X}, 0x{segment.Entry.End:X}) " +
                $"confirmed strictly increasing over {recordCount:N0} records");
        }
    }

    private int FindSegment(ulong address)
    {
        int lo = 0;
        int hi = _segmentsByStart.Length - 1;

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            SegmentIndexEntry entry = _segmentsByStart[mid].Entry;

            if (address < entry.Start)
                hi = mid - 1;
            else if (address >= entry.End)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

    /// <summary>
    /// Binary search over the segment's own scratch file — local record indices (0..RecordCount-1),
    /// not offset by <see cref="SegmentIndexEntry.FirstRecordIndex"/>, since this file was never
    /// concatenated into the merged column that offset is relative to.
    /// </summary>
    private static long FindRecord(OpenSegment segment, ulong address)
    {
        long lo = 0;
        long hi = segment.Entry.RecordCount - 1;

        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            ulong candidate = segment.AddressAccessor.ReadUInt64(mid * ColumnSize);

            if (address < candidate)
                hi = mid - 1;
            else if (address > candidate)
                lo = mid + 1;
            else
                return mid;
        }

        return -1;
    }

    /// <summary>
    /// §10.8 measurement pass confirmed the per-node <see cref="TryGetEntry"/> path (called once per
    /// reachable node in BFS discovery order, not address order) was the single largest sub-phase of
    /// Stage B's metadata resolution — its random-access binary search per node repeatedly re-walks
    /// each segment's mmap and thrashes the page cache at multi-million-node scale, even though every
    /// segment is confirmed address-sorted (see <see cref="VerifySegmentMonotonicity"/>). This resolves
    /// every <paramref name="addresses"/> entry with one O(N log N) sort plus a single O(N + total
    /// records) sequential merge across all segments (also sorted by <see cref="FindSegment"/>'s same
    /// start-address order) instead of N random-access binary searches — addresses not covered by any
    /// segment are left as the caller's existing default in <paramref name="methodTablesOut"/>/
    /// <paramref name="sizesOut"/>, same "not an error" contract as <see cref="TryGetEntry"/>.
    /// </summary>
    public void ResolveBatch(ulong[] addresses, ulong[] methodTablesOut, ulong[] sizesOut, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int n = addresses.Length;
        var sortedAddresses = new ulong[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            sortedAddresses[i] = addresses[i];
            order[i] = i;
        }
        Array.Sort(sortedAddresses, order);

        int queryIdx = 0;
        foreach (OpenSegment segment in _segmentsByStart)
        {
            while (queryIdx < n && sortedAddresses[queryIdx] < segment.Entry.Start)
                queryIdx++;

            long recordIndex = 0;
            long recordCount = segment.Entry.RecordCount;
            while (queryIdx < n && sortedAddresses[queryIdx] < segment.Entry.End)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ulong queryAddress = sortedAddresses[queryIdx];
                while (recordIndex < recordCount &&
                    segment.AddressAccessor.ReadUInt64(recordIndex * ColumnSize) < queryAddress)
                {
                    recordIndex++;
                }

                if (recordIndex >= recordCount)
                    break;

                if (segment.AddressAccessor.ReadUInt64(recordIndex * ColumnSize) == queryAddress)
                {
                    long byteOffset = recordIndex * ColumnSize;
                    int originalId = order[queryIdx];
                    methodTablesOut[originalId] = segment.MethodTableAccessor.ReadUInt64(byteOffset);
                    sizesOut[originalId] = segment.SizeAccessor.ReadUInt64(byteOffset);
                }

                queryIdx++;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (OpenSegment segment in _segmentsByStart)
        {
            segment.AddressAccessor.Dispose();
            segment.AddressFile.Dispose();
            segment.MethodTableAccessor.Dispose();
            segment.MethodTableFile.Dispose();
            segment.SizeAccessor.Dispose();
            segment.SizeFile.Dispose();
        }
    }
}
