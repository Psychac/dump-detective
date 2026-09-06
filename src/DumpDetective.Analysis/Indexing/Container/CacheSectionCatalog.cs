namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Whether a section's absence from a finished <c>cache.bin</c> means the build is broken, or
/// merely that this build wasn't configured to produce it.
/// </summary>
internal enum CacheSectionRequirement
{
    /// <summary>
    /// Written unconditionally by every successful build. Absence means a write failed and was
    /// downgraded to a warning, leaving a container that would otherwise pass the cache-hit fast
    /// path forever — the gap described in
    /// docs/cache/cache-implementation-clean-slate-redesign.md § 3.
    /// </summary>
    Required,

    /// <summary>
    /// May legitimately be absent from a container written by a healthy build — because Stage B
    /// wasn't gated on, because the section post-dates that container's format version, or because
    /// its write can fail repeatably at scale by design. Absence is therefore indistinguishable from
    /// "not produced", so it cannot be validated by presence alone. See
    /// <see cref="CacheSectionCatalog"/>'s remarks for which reason applies to which section.
    /// </summary>
    Conditional,

    /// <summary>Reserved enum slot with no writer and no reader in current code — never expected.</summary>
    Unused,
}

/// <summary>One section's identity and validation expectations, in one place for both directions.</summary>
internal readonly record struct CacheSectionDescriptor(
    CacheSectionId Id,
    string Name,
    CacheSectionRequirement Requirement);

/// <summary>
/// The single list of every section <c>cache.bin</c> can contain — see
/// docs/cache/cache-implementation-clean-slate-redesign.md § 6.2. Deliberately hand-written rather
/// than source-generated: one file is enough to make writer/reader drift visible, and it adds no
/// build-time machinery.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot validate.</b> § 3 wanted the cache-hit fast path to confirm that
/// every section a previous build wrote is still there. Checking "every section the TOC lists is
/// present" is vacuous — the TOC only ever lists sections that were successfully closed, so a
/// section lost to a transient write failure leaves no trace to compare against.
/// </para>
/// <para>
/// So the catalog splits sections by <see cref="CacheSectionRequirement"/> and the fast path asserts
/// only the <see cref="CacheSectionRequirement.Required"/> ones. That catches a core section lost to
/// a disk-full or AV blip, which is the case that silently degrades every future analysis of a dump.
/// It does <b>not</b> catch a lost <i>conditional</i> section, because absence there is
/// indistinguishable from "this build wasn't asked to produce it" — closing that needs the writer to
/// persist a manifest of intended sections, which is a format change and is not attempted here.
/// </para>
/// <para>
/// <b>Why some always-written sections are still Conditional.</b> The five <c>DD_SKIP_*</c> build
/// toggles were deleted (see § 6.2.1 of the redesign doc), so every section below is now written
/// unconditionally as far as *configuration* goes. Two things still keep sections out of
/// <see cref="CacheSectionRequirement.Required"/>:
/// </para>
/// <para>
/// <i>Backwards compatibility.</i> <see cref="CacheSectionId.RootStackThreadAttribution"/> was added
/// additively without a <c>CacheFileHeader.CurrentFormatVersion</c> bump, so v4 containers written
/// before it existed legitimately lack it — one such cache is on disk for a 27.5 GB dump. Marking it
/// Required would silently invalidate those and force a full cold re-index. Promoting it therefore
/// has to ride along with a format-version bump.
/// </para>
/// <para>
/// <i>Deterministic failure at scale.</i> The <c>ReverseEdge*</c> sections depend on the
/// reachability walk and the bucket sorts, which can fail repeatably on a very large heap (the
/// <c>ChunkedBuffer</c> int-overflow guard, or OOM) and are caught and downgraded to a warning by
/// design. Marking them Required would turn that into an unbreakable loop: cache rejected → full
/// rebuild → same failure → cache rejected, paying a cold build every single run. Silent degradation
/// is the lesser evil there, which is precisely why they stay Conditional.
/// </para>
/// <para>
/// <i>No longer written.</i> The three <c>ForwardEdge*</c> sections are
/// <see cref="CacheSectionRequirement.Unused"/> rather than Conditional: forward-edge extraction
/// still runs (Stage A's reachability walk needs it) but its output is no longer merged into the
/// container, because nothing ever read it — 462.4 MiB, 33% of <c>cache.bin</c>, on the reference
/// dump (docs/cache/cache-redesign-measurements.md §9). The ids stay reserved and
/// <c>ForwardEdgeContainerWriter</c> stays in place, so a future cache-hit-time consumer can restore
/// the merge with one call; containers written before this change still carry the sections, which is
/// harmless since nothing reads them.
/// </para>
/// <para>
/// <see cref="CacheSectionId.DominatorReachableAddresses"/> and its siblings stay Conditional for a
/// third reason that no toggle removal touches: Stage B is gated on an analyzer implementing
/// <c>IRequiresDominatorTreeIndex</c>, a genuine per-run condition.
/// </para>
/// <para>
/// <i>No longer written, second case.</i> <see cref="CacheSectionId.DominatorChildOffsets"/> and
/// <see cref="CacheSectionId.DominatorChildAddresses"/> are <see cref="CacheSectionRequirement.Unused"/>
/// for the same reason the <c>ForwardEdge*</c> sections are: format v7 derives the dominator child
/// direction on demand by inverting <see cref="CacheSectionId.DominatorImmediateDominatorAddresses"/>
/// in memory (<c>DominatorChildIndexReader</c>) instead of reading a persisted list — ~75 MiB on the
/// reference dump (docs/cache/cache-format-clean-slate-redesign.md §4, resolved by measurement in
/// cache-redesign-measurements.md §15). Unlike the <c>ForwardEdge*</c> case there is no writer left
/// producing these ids at all, so nothing restores the merge later; the ids stay reserved purely so a
/// v6 container's leftover sections are never misread as something else.
/// </para>
/// </remarks>
internal static class CacheSectionCatalog
{
    public static IReadOnlyList<CacheSectionDescriptor> All { get; } =
    [
        new(CacheSectionId.Objects, "Objects", CacheSectionRequirement.Unused),
        new(CacheSectionId.TypeAggregates, "TypeAggregates", CacheSectionRequirement.Required),
        new(CacheSectionId.Roots, "Roots", CacheSectionRequirement.Required),
        new(CacheSectionId.Handles, "Handles", CacheSectionRequirement.Conditional),
        new(CacheSectionId.Tasks, "Tasks", CacheSectionRequirement.Conditional),
        new(CacheSectionId.EventCandidates, "EventCandidates", CacheSectionRequirement.Unused),
        new(CacheSectionId.LargeObjects, "LargeObjects", CacheSectionRequirement.Conditional),
        new(CacheSectionId.LohFreeBlocks, "LohFreeBlocks", CacheSectionRequirement.Conditional),
        new(CacheSectionId.StringDedup, "StringDedup", CacheSectionRequirement.Conditional),
        new(CacheSectionId.StringDedupMeta, "StringDedupMeta", CacheSectionRequirement.Conditional),
        new(CacheSectionId.ObjectAddresses, "ObjectAddresses", CacheSectionRequirement.Required),
        new(CacheSectionId.ObjectMethodTables, "ObjectMethodTables", CacheSectionRequirement.Required),
        new(CacheSectionId.ObjectSizes, "ObjectSizes", CacheSectionRequirement.Required),
        new(CacheSectionId.ObjectGenerations, "ObjectGenerations", CacheSectionRequirement.Required),
        new(CacheSectionId.ReverseEdgeBuckets, "ReverseEdgeBuckets", CacheSectionRequirement.Conditional),
        new(CacheSectionId.ReverseEdgeDirectories, "ReverseEdgeDirectories", CacheSectionRequirement.Conditional),
        new(CacheSectionId.ReverseEdgeMetadata, "ReverseEdgeMetadata", CacheSectionRequirement.Conditional),
        new(CacheSectionId.SegmentIndex, "SegmentIndex", CacheSectionRequirement.Required),
        new(CacheSectionId.ForwardEdgeBuckets, "ForwardEdgeBuckets", CacheSectionRequirement.Unused),
        new(CacheSectionId.ForwardEdgeDirectories, "ForwardEdgeDirectories", CacheSectionRequirement.Unused),
        new(CacheSectionId.ForwardEdgeMetadata, "ForwardEdgeMetadata", CacheSectionRequirement.Unused),
        new(CacheSectionId.DominatorReachableAddresses, "DominatorReachableAddresses", CacheSectionRequirement.Conditional),
        new(CacheSectionId.DominatorImmediateDominatorAddresses, "DominatorImmediateDominatorAddresses", CacheSectionRequirement.Conditional),
        new(CacheSectionId.DominatorChildOffsets, "DominatorChildOffsets", CacheSectionRequirement.Unused),
        new(CacheSectionId.DominatorChildAddresses, "DominatorChildAddresses", CacheSectionRequirement.Unused),
        new(CacheSectionId.DominatorTreeMetadata, "DominatorTreeMetadata", CacheSectionRequirement.Conditional),
        new(CacheSectionId.DominatorRetainedBytes, "DominatorRetainedBytes", CacheSectionRequirement.Conditional),
        new(CacheSectionId.RootStackThreadAttribution, "RootStackThreadAttribution", CacheSectionRequirement.Conditional),
        new(CacheSectionId.ObjectTypeDictionary, "ObjectTypeDictionary", CacheSectionRequirement.Required),
        new(CacheSectionId.ObjectSizeOverflow, "ObjectSizeOverflow", CacheSectionRequirement.Conditional),
        new(CacheSectionId.ObjectAddressBlockBases, "ObjectAddressBlockBases", CacheSectionRequirement.Required),
        new(CacheSectionId.ObjectAddressOverflow, "ObjectAddressOverflow", CacheSectionRequirement.Required),
        new(CacheSectionId.DominatorReachableBlockBases, "DominatorReachableBlockBases", CacheSectionRequirement.Conditional),
        new(CacheSectionId.DominatorReachableOverflow, "DominatorReachableOverflow", CacheSectionRequirement.Conditional),
        new(CacheSectionId.SectionManifest, "SectionManifest", CacheSectionRequirement.Required),
    ];

    /// <summary>
    /// Sections that must be present in any container produced by a successful build. Used by the
    /// cache-hit fast path so a container missing one is rebuilt rather than reused forever.
    /// </summary>
    public static IReadOnlyList<CacheSectionDescriptor> Required { get; } =
        All.Where(d => d.Requirement == CacheSectionRequirement.Required).ToArray();

    /// <summary>
    /// Guards against a new <see cref="CacheSectionId"/> being added without a catalog entry — the
    /// drift this catalog exists to make visible. Verified by a unit test rather than at runtime.
    /// </summary>
    public static IReadOnlyList<CacheSectionId> MissingFromCatalog()
    {
        var known = All.Select(d => d.Id).ToHashSet();
        return Enum.GetValues<CacheSectionId>().Where(id => !known.Contains(id)).ToArray();
    }
}
