using DumpDetective.Analysis.Analyzers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionElementTypeAggregationTests
{
    [Fact]
    public void AccumulateElementTypeWaste_NewKey_InitializesCountAndBytes()
    {
        var counts = new Dictionary<string, int>();
        var bytes = new Dictionary<string, ulong>();

        CollectionAnalyzer.AccumulateElementTypeWaste(counts, bytes, "System.String", 1_000);

        counts["System.String"].Should().Be(1);
        bytes["System.String"].Should().Be(1_000ul);
    }

    [Fact]
    public void AccumulateElementTypeWaste_ExistingKey_AccumulatesAcrossCalls()
    {
        var counts = new Dictionary<string, int>();
        var bytes = new Dictionary<string, ulong>();

        CollectionAnalyzer.AccumulateElementTypeWaste(counts, bytes, "System.String", 1_000);
        CollectionAnalyzer.AccumulateElementTypeWaste(counts, bytes, "System.String", 500);

        counts["System.String"].Should().Be(2);
        bytes["System.String"].Should().Be(1_500ul);
    }

    [Fact]
    public void AccumulateElementTypeWaste_EmptyElementType_BucketsUnderUnknownLabel()
    {
        // WastefulCollection.ElementType defaults to "" when the component type couldn't be
        // resolved — this must not produce a blank dictionary key/report row.
        var counts = new Dictionary<string, int>();
        var bytes = new Dictionary<string, ulong>();

        CollectionAnalyzer.AccumulateElementTypeWaste(counts, bytes, string.Empty, 1_000);

        counts.Should().NotContainKey(string.Empty);
        counts.Keys.Should().ContainSingle().Which.Should().NotBeEmpty();
    }
}
