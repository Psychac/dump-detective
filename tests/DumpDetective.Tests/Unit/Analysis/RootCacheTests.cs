using Xunit;
using FluentAssertions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class RootCacheTests
{
    [Fact]
    public void GetOrBuildValidRoots_ThrowsOnNullHeap()
    {
        var cache = new RootCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetOrBuildValidRoots(null!));
    }

    [Fact]
    public void GetStaticRootedAddresses_ThrowsOnNullHeap()
    {
        var cache = new RootCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetStaticRootedAddresses(null!));
    }

    [Fact]
    public void GetRootDescription_ReturnsNullWhenAbsent()
    {
        var cache = new RootCache(() => null);
        cache.GetRootDescription(0x1234).Should().BeNull();
    }
}
