using DumpDetective.Analysis.Analyzers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionOwnerTypeHintTests
{
    [Fact]
    public void SingleParent_NotTruncated_ReturnsBareTypeName()
    {
        CollectionAnalyzer.FormatOwnerTypeHint(parentCount: 1, truncated: false, firstOwnerType: "MyApp.CacheManager")
            .Should().Be("MyApp.CacheManager");
    }

    [Fact]
    public void MultipleParents_ReportsCountAndExampleExplicitly()
    {
        // The reverse index has no notion of which parent is the "real" owner — never report one
        // arbitrary parent as if it were definitive.
        CollectionAnalyzer.FormatOwnerTypeHint(parentCount: 3, truncated: false, firstOwnerType: "MyApp.CacheManager")
            .Should().Be("3 referrers, e.g. MyApp.CacheManager");
    }

    [Fact]
    public void SingleParent_ButTruncated_StillReportsAsAmbiguous()
    {
        // truncated means the index couldn't fully extract the real parent list, even if only
        // one entry made it into the returned (partial) set — so it's a lower bound, not a fact.
        CollectionAnalyzer.FormatOwnerTypeHint(parentCount: 1, truncated: true, firstOwnerType: "MyApp.CacheManager")
            .Should().Be("1+ referrers, e.g. MyApp.CacheManager");
    }

    [Fact]
    public void MultipleParents_Truncated_UsesLowerBoundNotation()
    {
        CollectionAnalyzer.FormatOwnerTypeHint(parentCount: 5, truncated: true, firstOwnerType: "MyApp.CacheManager")
            .Should().Be("5+ referrers, e.g. MyApp.CacheManager");
    }

    [Fact]
    public void NoOwnerTypeResolved_ReturnsNull()
    {
        CollectionAnalyzer.FormatOwnerTypeHint(parentCount: 1, truncated: false, firstOwnerType: null)
            .Should().BeNull();
    }
}
