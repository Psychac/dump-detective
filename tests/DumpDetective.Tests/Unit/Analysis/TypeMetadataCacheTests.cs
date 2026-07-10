using Xunit;
using FluentAssertions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class TypeMetadataCacheTests
{
    [Fact]
    public void MethodTableHasOutgoingRefs_ThrowsOnNullHeap()
    {
        var cache = new TypeMetadataCache(() => null);
        Assert.Throws<ArgumentNullException>(() => cache.MethodTableHasOutgoingRefs(null!, 1));
    }

    [Fact]
    public void MethodTableHasOutgoingRefs_ReturnsFalseForZeroMethodTable()
    {
        var cache = new TypeMetadataCache(() => null);
        // methodTable == 0 should return false without requiring a real heap
        cache.MethodTableHasOutgoingRefs(null!, 0).Should().BeFalse();
    }
}
