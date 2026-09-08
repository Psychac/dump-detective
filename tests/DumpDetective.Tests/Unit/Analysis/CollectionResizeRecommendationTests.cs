using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionResizeRecommendationTests
{
    [Theory]
    [InlineData(CollectionKind.Dictionary)]
    [InlineData(CollectionKind.HashSet)]
    [InlineData(CollectionKind.List)]
    [InlineData(CollectionKind.Stack)]
    [InlineData(CollectionKind.SortedList)]
    [InlineData(CollectionKind.Queue)]
    public void ModeratelyFilled_RecommendsTrimExcess(CollectionKind kind)
    {
        string recommendation = CollectionAnalyzer.BuildResizeRecommendation(kind, count: 800, capacity: 1000, fillRate: 80.0);

        recommendation.Should().Contain("TrimExcess()");
    }

    [Fact]
    public void ArrayList_ModeratelyFilled_RecommendsTrimToSize()
    {
        string recommendation = CollectionAnalyzer.BuildResizeRecommendation(CollectionKind.ArrayList, count: 800, capacity: 1000, fillRate: 80.0);

        recommendation.Should().Contain("TrimToSize()");
    }

    [Fact]
    public void ImmutableArrayBuilder_RecommendsToImmutable_RegardlessOfFillRate()
    {
        string recommendation = CollectionAnalyzer.BuildResizeRecommendation(CollectionKind.ImmutableArrayBuilder, count: 5, capacity: 1000, fillRate: 0.5);

        recommendation.Should().Contain("ToImmutable()");
        recommendation.Should().NotContain("initial capacity");
    }

    [Fact]
    public void VerySparse_RecommendsRightSizedConstruction_NotTrim()
    {
        string recommendation = CollectionAnalyzer.BuildResizeRecommendation(CollectionKind.List, count: 5, capacity: 1000, fillRate: 0.5);

        recommendation.Should().Contain("initial capacity near 5");
        recommendation.Should().NotContain("TrimExcess()");
    }

    [Fact]
    public void NeverAssertsReachability()
    {
        // BuildResizeRecommendation takes no root/reachability signal by design — a budget-limited
        // root-path search can never justify a "no fix needed" verdict.
        string recommendation = CollectionAnalyzer.BuildResizeRecommendation(CollectionKind.Dictionary, count: 800, capacity: 1000, fillRate: 80.0);

        recommendation.Should().NotContain("unreachable", "reachability is never asserted from a budget-limited search");
        recommendation.Should().NotContain("no fix needed");
    }
}
