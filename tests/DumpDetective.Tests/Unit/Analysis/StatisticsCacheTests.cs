using Xunit;
using FluentAssertions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class StatisticsCacheTests
{
    [Fact]
    public void GetOrBuildTypeStatistics_ThrowsOnNullHeap()
    {
        var cache = new StatisticsCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetOrBuildTypeStatistics(null!));
    }

    [Fact]
    public void GetSampleInstanceAddress_ReturnsNullWhenAbsent()
    {
        var cache = new StatisticsCache(() => null);
        cache.GetSampleInstanceAddress("NoSuchType").Should().BeNull();
    }
}
