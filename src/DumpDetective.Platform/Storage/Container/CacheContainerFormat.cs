using System.Buffers.Binary;
using System.Text;

namespace DumpDetective.Platform.Storage.Container;

/// <summary>
/// Stable identifiers for each section stored in <c>cache.bin</c>. Values are persisted in the
/// TOC and must never be renumbered — appending new members is safe, reordering is not.
/// </summary>
internal enum CacheSectionId
{
    /// <summary>Unused since format version 2 — superseded by the columnar Object* sections below.</summary>
    Objects = 0,
    TypeAggregates = 1,
    Roots = 2,
    Handles = 3,
    Tasks = 4,
    EventCandidates = 5,
    LargeObjects = 6,
    LohFreeBlocks = 7,
    StringDedup = 8,
    StringDedupMeta = 9,
    /// <summary>Columnar <c>ulong[]</c> of object addresses, one per heap object.</summary>
    ObjectAddresses = 10,
    /// <summary>Columnar <c>ulong[]</c> of object method tables, aligned with <see cref="ObjectAddresses"/>.</summary>
    ObjectMethodTables = 11,
    /// <summary>Columnar <c>ulong[]</c> of object sizes, aligned with <see cref="ObjectAddresses"/>.</summary>
    ObjectSizes = 12,
    /// <summary>
    /// Reserved, no longer written (format v9) — superseded by <see cref="ObjectGenerationRuns"/>.
    /// Held a columnar <c>sbyte[]</c> of per-object GC generations, aligned with
    /// <see cref="ObjectAddresses"/>, at one byte per object.
    /// </summary>
    ObjectGenerations = 13,
    /// <summary>
    /// Reserved, no longer written (format v8). Held concatenated sorted-group payloads
    /// (<c>.dat</c>) from every reverse-edge hash bucket until true CSR
    /// (<see cref="ReverseEdgeOffsets"/>/<see cref="ReverseEdgeChildren"/>) replaced the whole
    /// hash-bucket-sort-directory format — docs/cache/cache-format-clean-slate-redesign.md §2.
    /// </summary>
    ReverseEdgeBuckets = 14,
    /// <summary>Reserved, no longer written — see <see cref="ReverseEdgeBuckets"/>.</summary>
    ReverseEdgeDirectories = 15,
    /// <summary>Reserved, no longer written — see <see cref="ReverseEdgeBuckets"/>.</summary>
    ReverseEdgeMetadata = 16,
    /// <summary>
    /// Small per-segment table of (Start, End, FirstRecordIndex, RecordCount) — see
    /// <see cref="Indexing.Satellite.SegmentIndexWriter"/> and
    /// docs/cache/cache-architecture.md. Enables <c>ObjectAddressLookup</c>'s
    /// binary-search point lookup (address → MethodTable/Size) without a container FormatVersion
    /// bump: a missing section here just means the disk-backed point lookup is unavailable and
    /// callers fall back to <c>heap.GetObject</c>, the same "absent section" contract every other
    /// optional satellite section already has.
    /// </summary>
    SegmentIndex = 17,
    /// <summary>
    /// Concatenated sorted-group payloads (<c>.dat</c>) from every forward-edge bucket, back to
    /// back in bucket order — mirrors <see cref="ReverseEdgeBuckets"/> but keyed by parent
    /// address instead of child, and uncapped (out-degree has no hub-fanout problem the way
    /// in-degree does — see docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D3).
    /// Added without a <see cref="CacheFileHeader.CurrentFormatVersion"/> bump, following
    /// <see cref="SegmentIndex"/>'s precedent: purely additive, always-optional.
    /// </summary>
    ForwardEdgeBuckets = 18,
    /// <summary>Directory-index payloads (<c>.idx</c>) for <see cref="ForwardEdgeBuckets"/>, mirroring <see cref="ReverseEdgeDirectories"/>.</summary>
    ForwardEdgeDirectories = 19,
    /// <summary>JSON <see cref="Indexing.ForwardIndex.ForwardIndexMetadata"/>: bucket count and per-bucket offsets/lengths into the two sections above.</summary>
    ForwardEdgeMetadata = 20,
    /// <summary>
    /// Reserved, no longer written (format v10) — superseded by <see cref="ReachableRowBitmap"/>,
    /// which records the same set as one bit per object row instead of a second copy of every
    /// reachable address. Held a columnar <c>ulong[]</c> of reachable-node addresses, sorted — every object Stage A's
    /// reachability walk found reachable from a GC root (§4/§7,
    /// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Written by
    /// <c>DominatorReachableAddressWriter</c> entirely inside Phase 1, before
    /// <see cref="Container.CacheContainerWriter.Finish"/> — the "write-once container" concern this
    /// comment used to raise never actually applied once everything runs before <c>Finish()</c>.
    /// </summary>
    DominatorReachableAddresses = 21,
    /// <summary>
    /// Columnar <c>uint[]</c> of each node's immediate-dominator *row* (not address) in
    /// <see cref="DominatorReachableAddresses"/>' ordering, or
    /// <see cref="Indexing.Dominator.DominatorRowIndex.NoParentRow"/> for a direct child of the
    /// virtual root. Row-indexed since format v7 — docs/cache/cache-format-clean-slate-redesign.md
    /// §4's aggressive option — specifically so <see cref="Indexing.Dominator.DominatorChildIndexReader"/>
    /// can invert this column into "what does this row dominate" in memory without a search per row;
    /// an address-keyed column couldn't be inverted that cheaply.
    /// </summary>
    DominatorImmediateDominatorAddresses = 22,
    /// <summary>
    /// Reserved, no longer written (format v7). Held a columnar <c>int[]</c> CSR of dominator-tree
    /// child offsets until <see cref="DominatorImmediateDominatorAddresses"/> became invertible in
    /// memory — see <see cref="Indexing.Dominator.DominatorChildIndexReader"/> and
    /// docs/cache/cache-format-clean-slate-redesign.md §4.
    /// </summary>
    DominatorChildOffsets = 23,
    /// <summary>Reserved, no longer written — see <see cref="DominatorChildOffsets"/>.</summary>
    DominatorChildAddresses = 24,
    /// <summary>JSON <see cref="Indexing.Dominator.DominatorTreeMetadata"/>: whole-tree total retained bytes and the per-<c>MethodTable</c> rollup (§10.4, Batch 2b).</summary>
    DominatorTreeMetadata = 25,
    /// <summary>
    /// Columnar <c>ulong[]</c> of each reachable node's exact retained bytes (subtree sum, folded
    /// leaves' own shallow size included), row-aligned with <see cref="DominatorReachableAddresses"/>
    /// — §10.4 (Batch 3, docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md).
    /// Persisted so <c>IDominatorTreeProvider.TryGetRetainedBytes</c> is a binary search instead of
    /// a per-query subtree walk.
    /// </summary>
    DominatorRetainedBytes = 26,
    /// <summary>
    /// Fixed <c>RootAddr(8) | OSThreadId(4) | ManagedThreadId(4)</c> records — §12.2
    /// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): which thread owns
    /// each Stack-kind GC root, resolved once at Phase 1 build time via
    /// <c>ClrThread.EnumerateStackRoots()</c> (a <c>ClrRoot</c> alone carries no thread identity).
    /// Independent of <see cref="Roots"/>'s own internal versioning — a brand new section rather
    /// than a second trailer bolted onto <see cref="Roots"/>'s single <c>IndexHeader.Reserved</c>
    /// slot, which the field-name trailer already spends.
    /// </summary>
    RootStackThreadAttribution = 27,
    /// <summary>
    /// Dense <c>ulong[]</c> of every distinct <c>MethodTable</c> in the heap, ascending. A row's
    /// position is its <c>TypeId</c>, which is what <see cref="ObjectMethodTables"/> stores instead
    /// of the full 8-byte pointer — 14,003 distinct types for 14.6M objects on the reference dump,
    /// so 2 bytes per object replaces 8 (docs/cache/cache-format-clean-slate-redesign.md §3).
    /// Required whenever <see cref="ObjectMethodTables"/> is present: the column is meaningless
    /// without it.
    /// </summary>
    ObjectTypeDictionary = 28,
    /// <summary>
    /// Sorted <c>RecordIndex(4) | Size(8)</c> pairs holding the object sizes too large for
    /// <see cref="ObjectSizes"/>' narrowed width, which stores an all-ones escape sentinel in their
    /// place (docs/cache/cache-format-clean-slate-redesign.md §10.2). Present but empty when the
    /// column narrowed with no escapes; absent when the writer kept the full 8-byte width, in which
    /// case there is nothing to escape to.
    /// </summary>
    ObjectSizeOverflow = 29,
    /// <summary>
    /// One 8-byte base address per 1024-record block of <see cref="ObjectAddresses"/>, which stores
    /// a 4-byte delta from its block's base rather than the full address
    /// (docs/cache/cache-format-clean-slate-redesign.md §10.3). Required whenever
    /// <see cref="ObjectAddresses"/> is 4 bytes wide.
    /// </summary>
    ObjectAddressBlockBases = 30,
    /// <summary>
    /// Sorted <c>RecordIndex(4) | Address(8)</c> pairs for the <see cref="ObjectAddresses"/> records
    /// whose delta from their block base doesn't fit 4 bytes — 16 of 14.6M on the reference dump,
    /// none of 87.1M on the 27.5 GB one.
    /// </summary>
    ObjectAddressOverflow = 31,
    /// <summary>Block bases for <see cref="DominatorReachableAddresses"/>; see <see cref="ObjectAddressBlockBases"/>.</summary>
    DominatorReachableBlockBases = 32,
    /// <summary>Escaped rows of <see cref="DominatorReachableAddresses"/>; see <see cref="ObjectAddressOverflow"/>.</summary>
    DominatorReachableOverflow = 33,
    /// <summary>
    /// Dense <c>int32[]</c> of every section id the build <i>intended</i> to write, recorded when
    /// each section was opened rather than when it closed. The TOC lists only sections that closed
    /// successfully, so on its own it cannot distinguish "this build wasn't asked to produce that
    /// section" from "that section's write failed and was downgraded to a warning" — diffing it
    /// against this manifest can (docs/cache/cache-format-clean-slate-redesign.md §10.5).
    /// </summary>
    SectionManifest = 34,
    /// <summary>
    /// Reserved, no longer written (format v9) — superseded by <see cref="ReverseEdgeDegrees"/> plus
    /// <see cref="ReverseEdgeDegreeCheckpoints"/>. Held columnar <c>int32[R+1]</c> CSR offsets into <see cref="ReverseEdgeChildren"/>, indexed by the
    /// same reachable-node row as <see cref="DominatorReachableAddresses"/> — format v8's true CSR
    /// replacement for the hash-bucket-sort-directory format
    /// (docs/cache/cache-format-clean-slate-redesign.md §2). Row <c>r</c>'s parents are
    /// <c>Children[Offsets[r]..Offsets[r+1]]</c> — a direct array slice, no directory, no second
    /// binary search.
    /// </summary>
    ReverseEdgeOffsets = 35,
    /// <summary>
    /// Flat <c>int32[]</c> column of parent *row indices* (not addresses), grouped by child row —
    /// see <see cref="ReverseEdgeOffsets"/>. Row indices rather than addresses is what makes this
    /// smaller than the retired directory-based format: no per-entry address, no per-key header.
    /// </summary>
    ReverseEdgeChildren = 36,
    /// <summary>
    /// Sorted <c>RecordIndex(4) | RetainedBytes(8)</c> pairs for the rows whose retained bytes
    /// exceed <see cref="DominatorRetainedBytes"/>' narrowed width, which stores an all-ones escape
    /// sentinel in their place — the same scheme <see cref="ObjectSizeOverflow"/> uses for
    /// <see cref="ObjectSizes"/>. 69–75% of rows are dominator-tree leaves whose retained bytes are
    /// their own shallow size, so the column narrows to 2 bytes at a 0.19% escape rate
    /// (docs/cache/cache-ideal-design.md §7.1).
    /// </summary>
    DominatorRetainedBytesOverflow = 37,
    /// <summary>
    /// One byte per reachable row: that row's recorded parent count, or
    /// <see cref="Indexing.ReverseIndex.ReverseEdgeDegreeColumn.EscapeSentinel"/> if it did not fit.
    /// Replaces <see cref="ReverseEdgeOffsets"/>' full-width offset column — the offsets are monotone
    /// with a mean step of 2.35, so 4 bytes a row held a number that nearly always fits in one
    /// (docs/cache/cache-ideal-design.md §3.2, O3).
    /// </summary>
    ReverseEdgeDegrees = 38,
    /// <summary>
    /// Absolute <c>int32</c> offset into <see cref="ReverseEdgeChildren"/> every
    /// <see cref="Indexing.ReverseIndex.ReverseEdgeDegreeColumn.CheckpointStride"/> rows, so a row's
    /// offset is one checkpoint plus a sum of at most 63 degree bytes rather than a scan from zero.
    /// </summary>
    ReverseEdgeDegreeCheckpoints = 39,
    /// <summary>
    /// Sorted <c>RecordIndex(4) | Degree(8)</c> pairs for rows whose in-degree reached
    /// <see cref="Indexing.ReverseIndex.ReverseEdgeDegreeColumn.EscapeSentinel"/> — hub objects.
    /// </summary>
    ReverseEdgeDegreeOverflow = 40,
    /// <summary>
    /// Run-length encoded per-object GC generation — one <c>FirstRecordIndex(8) | Generation(1) |
    /// Pad(3)</c> record per *change*, replacing <see cref="ObjectGenerations"/>' byte per object.
    /// Generation is piecewise-constant over the object table: measured 13 runs over 14,620,162
    /// objects and 50 over 87,104,236, so an 83.07 MiB column carried ~600 bytes of information
    /// (docs/cache/cache-ideal-design.md §3.2, O2).
    /// </summary>
    ObjectGenerationRuns = 41,
    /// <summary>
    /// Which object rows the reachability walk reached, as a bitmap over object rows plus a rank
    /// directory — see <see cref="DumpDetective.Platform.Storage.Columns.ReachableRowBitmap"/>. Replaces
    /// <see cref="DominatorReachableAddresses"/>, which stored every reachable address a second time
    /// (222.99 MiB with its bases and escape table on the 27.5 GB dump, against 10.70 MiB here).
    /// Reachable-row order is unchanged — ascending object row is ascending address, because the
    /// object column is monotonic by construction (O1).
    /// </summary>
    ReachableRowBitmap = 42,
    /// <summary>
    /// First trace-artifact section id (docs/refactor/modularity/phase-6-trace-source.md § Phase 6a
    /// — "trace.methods"). Written to a trace's own container file, never a dump's — container files
    /// are already one-per-artifact, so there is no collision risk sharing this id space rather than
    /// generalizing the writer/reader to a pluggable section-id type: a dump container never writes
    /// this id and a trace container never writes any of the ids above it. Fixed
    /// <c>MethodId(8) | ModuleId(8) | StartAddress(8) | Size(4) | TypeToken(4) | Flags(4) |
    /// DeclaringTypeCanonicalName(len-prefixed UTF-8) | Name(len-prefixed UTF-8) |
    /// NormalizedSignature(len-prefixed UTF-8)</c> records, one per distinct JIT'd or rundown method.
    /// </summary>
    TraceMethods = 43,
}

/// <summary>
/// Fixed 64-byte header at offset 0 of <c>cache.bin</c>.
/// </summary>
/// <remarks>
/// Layout (little-endian):
///   Offset  0 — Magic (8 bytes)            ASCII "DDCACHE1"
///   Offset  8 — FormatVersion (4 bytes)     int, readers reject unsupported versions
///   Offset 12 — DumpContentHash (32 bytes)  <see cref="DumpContentHasher"/> signature; zero-filled if unknown
///   Offset 44 — SectionCount (4 bytes)      int
///   Offset 48 — TocOffset (8 bytes)         long, always equal to <see cref="Size"/>
///   Offset 56 — Reserved (8 bytes)          zero
/// Total = 64 bytes
/// </remarks>
internal readonly struct CacheFileHeader
{
    public const int Size = 64;
    /// <summary>
    /// Bumped to 10 when the reachable-node row space stopped being a stored address column and
    /// became a derived index: <see cref="CacheSectionId.ReachableRowBitmap"/> (one bit per object
    /// row plus a rank directory) replaces <see cref="CacheSectionId.DominatorReachableAddresses"/>
    /// and its block bases and escape table, which together were a second copy of every reachable
    /// address — 222.99 MiB against 10.70 MiB on the 27.5 GB dump. Row order and every row-aligned
    /// column keyed by it are unchanged, because ascending object row is ascending address
    /// (docs/cache/cache-ideal-design.md §3.1, R1). A v9 reader would find no reachable column at
    /// all and silently lose the dominator tree and reverse index, so this bump is load-bearing.
    /// Previously bumped to 9 for three independent size changes that shared one bump, because each bump
    /// invalidates every cache on disk and paying that cost three times buys nothing
    /// (docs/cache/cache-redesign-measurements.md §10.2): <see cref="CacheSectionId.DominatorRetainedBytes"/>
    /// narrowed from a flat 8 bytes to the width its distribution allows (2 on both reference dumps,
    /// −332.59 MiB on the 27.5 GB one) with escapes in
    /// <see cref="CacheSectionId.DominatorRetainedBytesOverflow"/>; the reverse CSR's
    /// <see cref="CacheSectionId.ReverseEdgeOffsets"/> column replaced by
    /// <see cref="CacheSectionId.ReverseEdgeDegrees"/> +
    /// <see cref="CacheSectionId.ReverseEdgeDegreeCheckpoints"/> +
    /// <see cref="CacheSectionId.ReverseEdgeDegreeOverflow"/> (−159.96 MiB); and
    /// <see cref="CacheSectionId.ObjectGenerations"/> dropped entirely in favour of deriving
    /// generation from the segment table (−83.07 MiB). A v8 reader would misparse all three rather
    /// than fail, so this bump is load-bearing.
    /// Previously bumped to 8 when the reverse-reference index changed from an address-keyed
    /// hash-bucket-sort-directory format (<see cref="CacheSectionId.ReverseEdgeBuckets"/>/
    /// <see cref="CacheSectionId.ReverseEdgeDirectories"/>/<see cref="CacheSectionId.ReverseEdgeMetadata"/>)
    /// to true CSR (<see cref="CacheSectionId.ReverseEdgeOffsets"/>/<see cref="CacheSectionId.ReverseEdgeChildren"/>)
    /// — format doc §2, closing the largest remaining lever in the redesign. A v7 reader would
    /// misparse the flat CSR arrays as a JSON metadata blob plus grouped-address payloads rather
    /// than fail, so this bump is load-bearing, not cosmetic.
    /// Previously bumped to 7 when <see cref="CacheSectionId.DominatorImmediateDominatorAddresses"/> changed
    /// from an 8-byte dominator *address* per row to a 4-byte dominator *row* index, and the
    /// persisted dominator child list (<see cref="CacheSectionId.DominatorChildOffsets"/>/
    /// <see cref="CacheSectionId.DominatorChildAddresses"/>) stopped being written — format doc §4's
    /// aggressive option, resolved by measurement in cache-redesign-measurements.md §15. A v6 reader
    /// would read the narrowed idom column as garbage addresses rather than fail, so this bump is
    /// load-bearing, not cosmetic.
    /// Previously bumped to 6 when <see cref="CacheSectionId.ObjectSizes"/> changed from a fixed 8 bytes per
    /// object to the narrowest width that dump's size distribution allows, with
    /// <see cref="CacheSectionId.ObjectSizeOverflow"/> holding the values that don't fit
    /// (docs/cache/cache-format-clean-slate-redesign.md §10.2). A v5 reader would read the narrowed
    /// column as garbage sizes rather than fail, so this bump is load-bearing.
    /// Previously bumped to 5 when <see cref="CacheSectionId.ObjectMethodTables"/> changed from an 8-byte
    /// <c>MethodTable</c> per object to a narrow <c>TypeId</c> index into the new
    /// <see cref="CacheSectionId.ObjectTypeDictionary"/> section. A v4 reader would read the narrow
    /// column as garbage addresses rather than fail, so this bump is load-bearing, not cosmetic.
    /// Previously bumped to 4 when the ReverseEdgeBuckets/ReverseEdgeDirectories/ReverseEdgeMetadata
    /// sections were added for the disk-backed reverse-reference index — old cache.bin files
    /// fail <see cref="TryRead"/> and are rebuilt rather than misparsed.
    /// Previously bumped to 3 when the columnar ObjectGenerations section (per-object GC
    /// generation, 1 byte/sbyte) was added alongside ObjectAddresses/ObjectMethodTables/ObjectSizes.
    /// Previously bumped to 2 when the Objects section moved from an interleaved
    /// array-of-structs layout to those columnar sections.
    /// </summary>
    public const int CurrentFormatVersion = 10;

    private const int MagicOffset = 0;
    private const int MagicSize = 8;
    private const int FormatVersionOffset = MagicOffset + MagicSize;
    private const int DumpContentHashOffset = FormatVersionOffset + 4;
    private const int DumpContentHashSize = 32;
    private const int SectionCountOffset = DumpContentHashOffset + DumpContentHashSize;
    private const int TocOffsetOffset = SectionCountOffset + 4;

    private static readonly byte[] ExpectedMagic = Encoding.ASCII.GetBytes("DDCACHE1");

    public readonly int FormatVersion;
    public readonly byte[] DumpContentHash;
    public readonly int SectionCount;
    public readonly long TocOffset;

    public CacheFileHeader(int sectionCount, long tocOffset, byte[]? dumpContentHash = null)
    {
        FormatVersion = CurrentFormatVersion;
        DumpContentHash = dumpContentHash ?? new byte[DumpContentHashSize];
        SectionCount = sectionCount;
        TocOffset = tocOffset;
    }

    /// <summary>Writes this header into the first <see cref="Size"/> bytes of <paramref name="stream"/>.</summary>
    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        ExpectedMagic.CopyTo(buf);
        BinaryPrimitives.WriteInt32LittleEndian(buf[FormatVersionOffset..], FormatVersion);
        DumpContentHash.AsSpan().CopyTo(buf.Slice(DumpContentHashOffset, DumpContentHashSize));
        BinaryPrimitives.WriteInt32LittleEndian(buf[SectionCountOffset..], SectionCount);
        BinaryPrimitives.WriteInt64LittleEndian(buf[TocOffsetOffset..], TocOffset);
        stream.Write(buf);
    }

    /// <summary>
    /// Reads the header from the current stream position (must be at offset 0).
    /// Returns <c>false</c> if the stream is too short, the magic doesn't match, or the
    /// format version is unsupported.
    /// </summary>
    public static bool TryRead(Stream stream, out CacheFileHeader header)
    {
        Span<byte> buf = stackalloc byte[Size];
        int read = stream.ReadAtLeast(buf, Size, throwOnEndOfStream: false);
        if (read < Size || !buf.Slice(MagicOffset, MagicSize).SequenceEqual(ExpectedMagic))
        {
            header = default;
            return false;
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(buf[FormatVersionOffset..]);
        if (version != CurrentFormatVersion)
        {
            header = default;
            return false;
        }

        header = new CacheFileHeader(
            BinaryPrimitives.ReadInt32LittleEndian(buf[SectionCountOffset..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[TocOffsetOffset..]),
            buf.Slice(DumpContentHashOffset, DumpContentHashSize).ToArray());
        return true;
    }
}

/// <summary>
/// One 32-byte table-of-contents entry describing a single section's location in <c>cache.bin</c>.
/// </summary>
/// <remarks>
/// Layout (little-endian):
///   Offset  0 — SectionId (4 bytes)    <see cref="CacheSectionId"/>
///   Offset  4 — Offset (8 bytes)       absolute byte offset into cache.bin
///   Offset 12 — Length (8 bytes)       section byte length
///   Offset 20 — RecordCount (8 bytes)  number of records in the section
///   Offset 28 — Checksum (4 bytes)     XxHash32 of the section's bytes; validated lazily by
///                                      <see cref="CacheContainerReader.TryOpenSection"/> on first read
/// Total = 32 bytes
/// </remarks>
internal readonly struct CacheTocEntry
{
    public const int Size = 32;

    public readonly CacheSectionId SectionId;
    public readonly long Offset;
    public readonly long Length;
    public readonly long RecordCount;
    public readonly uint Checksum;

    public CacheTocEntry(CacheSectionId sectionId, long offset, long length, long recordCount, uint checksum)
    {
        SectionId = sectionId;
        Offset = offset;
        Length = length;
        RecordCount = recordCount;
        Checksum = checksum;
    }

    public void WriteTo(Stream stream)
    {
        Span<byte> buf = stackalloc byte[Size];
        BinaryPrimitives.WriteInt32LittleEndian(buf, (int)SectionId);
        BinaryPrimitives.WriteInt64LittleEndian(buf[4..], Offset);
        BinaryPrimitives.WriteInt64LittleEndian(buf[12..], Length);
        BinaryPrimitives.WriteInt64LittleEndian(buf[20..], RecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(buf[28..], Checksum);
        stream.Write(buf);
    }

    public static CacheTocEntry ReadFrom(ReadOnlySpan<byte> buf) =>
        new(
            (CacheSectionId)BinaryPrimitives.ReadInt32LittleEndian(buf),
            BinaryPrimitives.ReadInt64LittleEndian(buf[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[12..]),
            BinaryPrimitives.ReadInt64LittleEndian(buf[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buf[28..]));
}
