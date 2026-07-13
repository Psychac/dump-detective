using Xunit;
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
}
