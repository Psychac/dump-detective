using DumpDetective.Analysis.Traversal.Dominator;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

public sealed class ChunkedBufferTests
{
    [Fact]
    public void Add_ThenToArray_PreservesInsertionOrder()
    {
        var buffer = new ChunkedBuffer<int>();
        for (int i = 0; i < 1000; i++)
            buffer.Add(i * 2);

        buffer.Count.Should().Be(1000);
        int[] result = buffer.ToArray();
        result.Should().HaveCount(1000);
        for (int i = 0; i < 1000; i++)
            result[i].Should().Be(i * 2);
    }

    [Fact]
    public void Indexer_Get_ReturnsAddedValue()
    {
        var buffer = new ChunkedBuffer<ulong>();
        buffer.Add(0x1000UL);
        buffer.Add(0x2000UL);

        buffer[0].Should().Be(0x1000UL);
        buffer[1].Should().Be(0x2000UL);
    }

    [Fact]
    public void Indexer_Set_OverwritesExistingValue()
    {
        var buffer = new ChunkedBuffer<bool>();
        buffer.Add(false);
        buffer.Add(false);

        buffer[1] = true;

        buffer[0].Should().BeFalse();
        buffer[1].Should().BeTrue();
    }

    [Fact]
    public void CrossesMultipleChunkBoundaries_StillReadsAndWritesCorrectly()
    {
        // Chunk size is 1 << 16 (65,536) — exercise a count that spans several chunks, including
        // reads/writes right at a chunk boundary.
        const int count = 200_000;
        var buffer = new ChunkedBuffer<int>();
        for (int i = 0; i < count; i++)
            buffer.Add(i);

        buffer[65_535].Should().Be(65_535);
        buffer[65_536].Should().Be(65_536);
        buffer[131_072].Should().Be(131_072);
        buffer[count - 1].Should().Be(count - 1);

        buffer[65_536] = -1;
        buffer[65_536].Should().Be(-1);

        int[] array = buffer.ToArray();
        array.Should().HaveCount(count);
        array[65_536].Should().Be(-1);
        array[count - 1].Should().Be(count - 1);
    }

    [Fact]
    public void EmptyBuffer_ToArray_ReturnsEmptyArray()
    {
        var buffer = new ChunkedBuffer<int>();
        buffer.ToArray().Should().BeEmpty();
    }
}
