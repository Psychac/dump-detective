using DumpDetective.Analysis.Utilities;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class StackFrameRangeCorrelatorTests
{
    [Fact]
    public void FindOwningFrameIndex_ShouldReturnMiddleFrame_WhenSlotFallsWithinItsRange()
    {
        ulong[] stackPointers = { 0x1000, 0x1100, 0x1300 };

        int index = StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, 0x1150);

        index.Should().Be(1);
    }

    [Fact]
    public void FindOwningFrameIndex_ShouldReturnFirstFrame_WhenSlotEqualsItsStackPointer()
    {
        ulong[] stackPointers = { 0x1000, 0x1100, 0x1300 };

        StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, 0x1000).Should().Be(0);
    }

    [Fact]
    public void FindOwningFrameIndex_ShouldReturnOutermostFrame_WhenSlotIsBeyondLastFrame()
    {
        ulong[] stackPointers = { 0x1000, 0x1100, 0x1300 };

        // The outermost frame's range is unbounded (no next frame to cap it).
        StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, 0x9999).Should().Be(2);
    }

    [Fact]
    public void FindOwningFrameIndex_ShouldReturnMinusOne_WhenSlotIsBelowInnermostFrame()
    {
        ulong[] stackPointers = { 0x1000, 0x1100, 0x1300 };

        StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, 0x0FFF).Should().Be(-1);
    }

    [Fact]
    public void FindOwningFrameIndex_ShouldReturnMinusOne_WhenListIsEmpty()
    {
        StackFrameRangeCorrelator.FindOwningFrameIndex(Array.Empty<ulong>(), 0x1000).Should().Be(-1);
    }

    [Fact]
    public void FindOwningFrameIndex_ShouldReturnLastMatchingBoundary_WhenMultipleFramesShareAStackPointer()
    {
        // Two frames returning the same StackPointer is a documented ClrMD corner case
        // (EnumerateStackTrace's own doc). The later (outer) one should win.
        ulong[] stackPointers = { 0x1000, 0x1100, 0x1100, 0x1200 };

        StackFrameRangeCorrelator.FindOwningFrameIndex(stackPointers, 0x1100).Should().Be(2);
    }

    [Theory]
    [InlineData(new ulong[] { }, true)]
    [InlineData(new ulong[] { 0x100 }, true)]
    [InlineData(new ulong[] { 0x100, 0x200, 0x300 }, true)]
    [InlineData(new ulong[] { 0x100, 0x100, 0x300 }, true)]
    [InlineData(new ulong[] { 0x300, 0x200, 0x100 }, false)]
    [InlineData(new ulong[] { 0x100, 0x300, 0x200 }, false)]
    public void IsSortedAscending_ShouldDetectOrderCorrectly(ulong[] stackPointers, bool expected)
    {
        StackFrameRangeCorrelator.IsSortedAscending(stackPointers).Should().Be(expected);
    }
}
