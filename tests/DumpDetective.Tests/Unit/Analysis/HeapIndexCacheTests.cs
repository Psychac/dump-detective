using Xunit;
using FluentAssertions;
using DumpDetective.Analysis.Cache;

namespace DumpDetective.Tests.Unit.Analysis;

public class HeapIndexCacheTests
{
    [Fact]
    public void NewCache_HasNoIndexAndEnumeratesEmpty()
    {
        var cache = new HeapIndexCache();

        bool has = cache.TryGetHeapIndex(out var heapIndex);
        has.Should().BeFalse();
        heapIndex.Should().BeNull();

        cache.EnumerateIndexedEntriesAsTuples().Should().BeEmpty();
    }
}
