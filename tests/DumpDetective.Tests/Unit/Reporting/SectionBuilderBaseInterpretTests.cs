using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// docs/refactor/narrative-interpretation-text-design.md: <c>SectionBuilderBase.Interpret</c> is
/// <c>protected static</c>, so tests go through a minimal exposing subclass rather than reflection.
/// </summary>
public sealed class SectionBuilderBaseInterpretTests
{
    private sealed class TestSectionBuilder : SectionBuilderBase
    {
        public static InterpretationBlock? CallInterpret(double? value, params (double Threshold, string Text)[] tiers) =>
            Interpret(value, tiers);
    }

    private static readonly (double Threshold, string Text)[] Tiers =
    [
        (3.0, "high"),
        (1.2, "medium"),
        (0.0, "low"),
    ];

    [Fact]
    public void Interpret_NullValue_ReturnsNull()
    {
        TestSectionBuilder.CallInterpret(null, Tiers).Should().BeNull();
    }

    [Fact]
    public void Interpret_EmptyTiers_ReturnsNull()
    {
        TestSectionBuilder.CallInterpret(5.0).Should().BeNull();
    }

    [Fact]
    public void Interpret_ValueAboveHighestThreshold_ReturnsFirstTier()
    {
        TestSectionBuilder.CallInterpret(10.0, Tiers)!.Text.Should().Be("high");
    }

    [Fact]
    public void Interpret_ValueExactlyAtThreshold_TierIsInclusive()
    {
        TestSectionBuilder.CallInterpret(3.0, Tiers)!.Text.Should().Be("high");
        TestSectionBuilder.CallInterpret(1.2, Tiers)!.Text.Should().Be("medium");
    }

    [Fact]
    public void Interpret_ValueBetweenTiers_ReturnsLowerTier()
    {
        TestSectionBuilder.CallInterpret(2.0, Tiers)!.Text.Should().Be("medium");
    }

    [Fact]
    public void Interpret_ValueBelowAllThresholds_FallsThroughToFloorTier()
    {
        TestSectionBuilder.CallInterpret(-5.0, Tiers)!.Text.Should().Be("low");
    }
}
