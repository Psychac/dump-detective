using Xunit;
using FluentAssertions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class ThreadCacheTests
{
    [Fact]
    public void GetOrCountThreadStackRoots_ThrowsOnNullThread()
    {
        var cache = new ThreadCache();
        Assert.Throws<ArgumentNullException>(() => cache.GetOrCountThreadStackRoots(null!, 10));
    }
}
