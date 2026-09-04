using DumpDetective.Analysis.Indexing.Container;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing.Container;

/// <summary>
/// Guards the catalog introduced in docs/cache/cache-implementation-clean-slate-redesign.md § 6.2.
/// Its whole purpose is to make writer/reader drift visible in one place, which only holds if the
/// catalog itself stays in step with <see cref="CacheSectionId"/>.
/// </summary>
public class CacheSectionCatalogTests
{
    [Fact]
    public void Catalog_CoversEverySectionId()
    {
        CacheSectionCatalog.MissingFromCatalog().Should().BeEmpty(
            "a new CacheSectionId without a catalog entry is exactly the drift this catalog exists to catch");
    }

    [Fact]
    public void Catalog_HasNoDuplicateIds()
    {
        CacheSectionCatalog.All.Select(d => d.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The Required set drives the cache-hit fast path, so widening it silently would start
    /// rejecting containers that are actually fine — including ones already on disk. These are the
    /// sections <c>DiskBackedObjectIndexWriter</c> writes unconditionally *and* that every existing
    /// v4 container is known to contain.
    /// </summary>
    [Fact]
    public void Required_IsExactlyTheAlwaysWrittenAndAlwaysPresentSections()
    {
        CacheSectionCatalog.Required.Select(d => d.Id).Should().BeEquivalentTo(new[]
        {
            CacheSectionId.ObjectAddresses,
            CacheSectionId.ObjectMethodTables,
            CacheSectionId.ObjectSizes,
            CacheSectionId.ObjectGenerations,
            CacheSectionId.TypeAggregates,
            CacheSectionId.Roots,
            CacheSectionId.SegmentIndex,
        });
    }

    /// <summary>
    /// Sections that may legitimately be absent must stay Conditional. Marking one Required makes
    /// every container lacking it look corrupt — which for the edge sections would mean an
    /// unbreakable rebuild loop on a dump whose walk fails repeatably, and for
    /// <c>RootStackThreadAttribution</c> would invalidate v4 caches written before it was added.
    /// </summary>
    [Fact]
    public void EscapeHatchGatedSections_AreConditional()
    {
        CacheSectionId[] gated =
        [
            CacheSectionId.ReverseEdgeBuckets,           // can fail deterministically at scale
            CacheSectionId.ForwardEdgeBuckets,           // can fail deterministically at scale
            CacheSectionId.DominatorReachableAddresses,  // Stage B analyzer gating
            CacheSectionId.RootStackThreadAttribution,   // pre-dates its own additive introduction in some v4 caches
        ];

        foreach (CacheSectionId id in gated)
        {
            CacheSectionCatalog.All.Single(d => d.Id == id).Requirement
                .Should().Be(CacheSectionRequirement.Conditional, $"{id} may legitimately be absent");
        }
    }

    /// <summary>
    /// <c>EventCandidates</c> has no writer, reader, or collection in current code, and
    /// <c>Objects</c> has been superseded by the columnar sections since format v2 — both slots stay
    /// reserved so ids are never renumbered (that would misparse existing caches), but neither may
    /// be treated as expected.
    /// </summary>
    [Fact]
    public void ReservedButUnwrittenSections_AreUnused()
    {
        foreach (CacheSectionId id in new[] { CacheSectionId.Objects, CacheSectionId.EventCandidates })
        {
            CacheSectionCatalog.All.Single(d => d.Id == id).Requirement
                .Should().Be(CacheSectionRequirement.Unused, $"{id} has no writer or reader in current code");
        }
    }
}
