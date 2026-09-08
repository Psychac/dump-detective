using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// docs/analysis/phase1/dominator-analyzer-audit.md's "Shared Next steps" P3 item.
/// <c>SectionBuilderBase.NextSteps</c> is <c>protected static</c>, so tests go through a minimal
/// exposing subclass rather than reflection — same pattern as <see cref="SectionBuilderBaseInterpretTests"/>.
/// </summary>
public sealed class SectionBuilderBaseNextStepsTests
{
    private sealed class TestSectionBuilder : SectionBuilderBase
    {
        public static NextStepsBlock? CallNextSteps(params (string Label, string AnalyzerName)[] targets) =>
            NextSteps(targets);
    }

    [Fact]
    public void NextSteps_KnownAnalyzerWithSectionId_ResolvesLink()
    {
        NextStepsBlock? block = TestSectionBuilder.CallNextSteps(("Check GC roots", "GCRootAnalyzer"));

        block.Should().NotBeNull();
        block!.Links.Should().ContainSingle();
        block.Links[0].Label.Should().Be("Check GC roots");
        block.Links[0].SectionId.Should().Be("A5");
    }

    [Fact]
    public void NextSteps_UnknownAnalyzerName_SkipsThatTargetOnly()
    {
        NextStepsBlock? block = TestSectionBuilder.CallNextSteps(
            ("Check GC roots", "GCRootAnalyzer"),
            ("Unresolvable", "NoSuchAnalyzer"));

        block.Should().NotBeNull();
        block!.Links.Should().ContainSingle();
        block.Links[0].Label.Should().Be("Check GC roots");
    }

    [Fact]
    public void NextSteps_AllTargetsUnresolvable_ReturnsNull()
    {
        TestSectionBuilder.CallNextSteps(("Unresolvable", "NoSuchAnalyzer")).Should().BeNull();
    }

    [Fact]
    public void NextSteps_NoTargets_ReturnsNull()
    {
        TestSectionBuilder.CallNextSteps().Should().BeNull();
    }

    [Fact]
    public void NextSteps_MultipleResolvableTargets_PreservesOrder()
    {
        NextStepsBlock? block = TestSectionBuilder.CallNextSteps(
            ("Check GC roots", "GCRootAnalyzer"),
            ("Trace reference chains", "ReferenceChainAnalyzer"));

        block!.Links.Should().HaveCount(2);
        block.Links[0].SectionId.Should().Be("A5");
        block.Links[1].SectionId.Should().Be("A4");
    }
}
