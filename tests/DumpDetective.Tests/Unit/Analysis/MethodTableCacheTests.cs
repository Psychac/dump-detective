using FluentAssertions;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public class MethodTableCacheTests
{
    [Fact]
    public void GetTypeByMethodTable_ThrowsOnNullHeap()
    {
        var cache = new MethodTableCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.GetTypeByMethodTable(null!, 1UL));
    }

    [Fact]
    public void GetTypeByMethodTable_ReturnsNullForZeroMethodTable()
    {
        var cache = new MethodTableCache(() => null);
        cache.GetTypeByMethodTable(null!, 0UL).Should().BeNull();
    }
}
