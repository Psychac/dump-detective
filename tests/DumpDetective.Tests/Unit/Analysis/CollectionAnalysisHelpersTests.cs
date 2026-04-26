using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CollectionAnalysisHelpersTests
{
    [Fact]
    public void ComputeQueueFreeSegments_EmptyBuffer_ReturnsSingleFullFreeSegment()
    {
        var (segs, largest) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity: 10, size: 0, head: 0);
        segs.Should().Be(1);
        largest.Should().Be(10);
    }

    [Fact]
    public void ComputeQueueFreeSegments_NoHead_KnownSize_ReturnsConservativeEstimate()
    {
        var (segs, largest) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity: 10, size: 5, head: null);
        segs.Should().Be(1);
        largest.Should().Be(5);
    }

    [Fact]
    public void ComputeQueueFreeSegments_NonWrappedUsedRegion_CalculatesTwoSegments()
    {
        // head = 3, size = 5 -> used [3..7], free before=3, after=2
        var (segs, largest) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity: 10, size: 5, head: 3);
        segs.Should().Be(2);
        largest.Should().Be(3);
    }

    [Fact]
    public void ComputeQueueFreeSegments_UsedWraps_CalculatesSingleFreeSegment()
    {
        // head = 8, size = 5 -> used [8,9,0,1,2] endIndex = 2 free between 3..7 -> length 5
        var (segs, largest) = CollectionAnalysisHelpers.ComputeQueueFreeSegments(capacity: 10, size: 5, head: 8);
        segs.Should().Be(1);
        largest.Should().Be(5);
    }

    [Fact]
    public void ComputeWastedMemoryFromSlots_ComputesCorrectly()
    {
        ulong wasted = CollectionAnalysisHelpers.ComputeWastedMemoryFromSlots(capacity: 10, count: 5, elementSize: 8UL);
        wasted.Should().Be(40UL);
    }
}
