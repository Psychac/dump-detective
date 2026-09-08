using Xunit;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class RootSetCacheTests
{
    [Fact]
    public void GetOrBuildValidRoots_ThrowsOnNullHeap()
    {
        var cache = new RootSetCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetOrBuildValidRoots(null!));
    }

    [Fact]
    public void GetStaticRootedAddresses_ThrowsOnNullHeap()
    {
        var cache = new RootSetCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetStaticRootedAddresses(null!));
    }

    [Fact]
    public void GetPinnedRootedAddresses_ThrowsOnNullHeap()
    {
        var cache = new RootSetCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetPinnedRootedAddresses(null!));
    }

    [Theory]
    [InlineData(3, true)]  // PinnedHandle
    [InlineData(7, true)]  // AsyncPinnedHandle
    [InlineData(2, false)] // StrongHandle
    [InlineData(9, false)] // ThreadStaticVar
    [InlineData(10, false)] // StaticVar
    public void RootRecord_IsPinned_MatchesPinnedHandleKinds(byte kind, bool expected)
    {
        var record = new RootRecord(TargetAddr: 0x1000, RootAddr: 0x2000, Kind: kind);
        Assert.Equal(expected, record.IsPinned);
    }
}
