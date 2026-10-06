using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ClaimListingTests
{
    [Fact]
    public void Build_SkipsFindingsWithoutAnAssertion_ListsTheRest()
    {
        List<ClaimListingEntry> entries = ClaimListing.Build([Record("plain"), StructuredRecord()]);

        entries.Should().ContainSingle().Which.Title.Should().Be("structured title");
    }

    [Fact]
    public void Build_PartsCarryTheWordingTheHtmlCardUses()
    {
        ClaimListingEntry entry = ClaimListing.Build([StructuredRecord()]).Single();

        entry.Severity.Should().Be("Warning");
        entry.Assertion.Should().Be("Fragments: LOH/POH at 22.4%.");
        entry.Confidence.Should().Be("Medium (0.50)");
        entry.ConfidenceReason.Should().StartWith("Limited by the data (0.50)");
        entry.Support.Should().Equal("why one");
        entry.Counter.Should().Equal("None found: looked, found nothing");
        entry.Tests.Should().ContainSingle().Which.Should().Be(new ClaimListingTest("[windbg] check it", "!x"));
        entry.Recommendation.Should().Be("do the thing");
    }

    [Fact]
    public void MarkdownAndText_ListTheClaim_AndStayUnchangedWhenThereIsNone()
    {
        SingleDumpReportDocument withClaim = new() { DumpPath = "C:/d.dmp", Findings = [StructuredRecord()] };
        SingleDumpReportDocument without = new() { DumpPath = "C:/d.dmp", Findings = [Record("plain")] };

        string markdown = new MarkdownCanonicalReportFormatter().Render(withClaim);
        string text = new TextCanonicalReportFormatter().Render(withClaim);

        markdown.Should().Contain("## Findings").And.Contain("### [Warning] structured title")
            .And.Contain("Fragments: LOH/POH at 22.4%.").And.Contain("- why one")
            .And.Contain("- None found: looked, found nothing").And.Contain("- [windbg] check it — `!x`");
        text.Should().Contain("FINDINGS").And.Contain("[Warning] structured title")
            .And.Contain("Fragments: LOH/POH at 22.4%.").And.Contain("- why one")
            .And.Contain("- [windbg] check it -> !x").And.Contain("Recommendation: do the thing");
        new MarkdownCanonicalReportFormatter().Render(without).Should().NotContain("## Findings");
        new TextCanonicalReportFormatter().Render(without).Should().NotContain("FINDINGS");
    }

    private static FindingRecord Record(string title) =>
        new("id", "Analyzer", "Category", "Warning", title, ["details"], "recommendation", []);

    private static FindingRecord StructuredRecord() => Record("structured title") with
    {
        Recommendation = "do the thing",
        Assertion = new Assertion
        {
            Predicate = AssertionPredicate.Fragments,
            Measure = new Measure(22.4, MeasureUnit.Percent, MeasureSemantics.Ratio),
            Qualifiers = new AssertionQualifiers { Location = "LOH/POH" },
        },
        ConfidenceBreakdown = new ConfidenceBreakdown
        {
            Composite = 0.5,
            EvidenceStrength = 1.0,
            IdentityFidelityCap = 1.0,
            TemporalAlignmentCap = 1.0,
            CapabilityFidelity = 0.5,
            ConflictPenalty = 1.0,
        },
        Support = [new EvidenceStatement([ObservationId.NewId()], "why one")],
        Counter = Counter.None("looked, found nothing"),
        FalsifiedBy = [new FalsificationTest { Kind = FalsificationKind.Windbg, Test = "check it", Command = "!x" }],
    };
}
