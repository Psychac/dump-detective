using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class CoverageProseTests
{
    private static readonly ArtifactId Dump = new("dump-1");

    [Fact]
    public void Build_ReturnsNull_WhenThereAreNoRecords()
    {
        CoverageProse.Build(null).Should().BeNull();
        CoverageProse.Build([]).Should().BeNull();
    }

    [Fact]
    public void Build_AllComplete_SaysSoAndListsNoOtherGroup()
    {
        CoverageView view = CoverageProse.Build([Complete("heap.types"), Complete("heap.objects")])!;

        view.Headline.Should().Be("Coverage: 2 of 2 capabilities complete; no analyzer failed.");
        view.Groups.Should().ContainSingle().Which.Kind.Should().Be("complete");
    }

    [Fact]
    public void Build_PartialRecord_IsNamedInTheHeadlineWithItsBoundary_AndLinksItsClaims()
    {
        CoverageRecord partial = new(
            new Capability("heap.root-paths"), Dump, CoverageStatus.Partial,
            [new CoverageImpact("Reference Chain Analysis", CoverageImpactKind.Limited)],
            new CoverageBoundary(8, 10, "sampled types", "hit the path or depth budget"), Claims: ["reference chain analysis::retention-coverage"]);

        CoverageView view = CoverageProse.Build([Complete("heap.types"), partial])!;

        view.Headline.Should().Contain("1 of 2 capabilities complete")
            .And.Contain("1 limited (heap.root-paths: 8 of 10 sampled types hit the path or depth budget)");
        CoverageLineView line = view.Groups.Single(g => g.Kind == "limited").Lines.Should().ContainSingle().Subject;
        line.Claims.Should().Equal("reference chain analysis::retention-coverage");
        line.Text.Should().Contain("Reference Chain Analysis");
    }

    [Fact]
    public void Build_KeepsANeverSuppliedFamilyApartFromARealGap()
    {
        CoverageRecord notSupplied = new(
            new Capability("trace.gc-events"), null, CoverageStatus.NotSupplied,
            [new CoverageImpact("GcPauseAnalyzer", CoverageImpactKind.NotRun), new CoverageImpact("Heap Topology", CoverageImpactKind.ReducedFidelity, FidelityBoost.Moderate)]);
        CoverageRecord gap = new(
            new Capability("runtime.locks"), null, CoverageStatus.Unavailable,
            [new CoverageImpact("Lock Graph Analysis", CoverageImpactKind.NotRun)]);

        CoverageView view = CoverageProse.Build([Complete("heap.types"), gap, notSupplied])!;

        view.Headline.Should().Contain("Not supplied: trace (1 capability, 1 analyzer(s) not run, 1 at reduced fidelity).");
        view.Headline.Should().NotContain("runtime.locks");
        view.Groups.Select(g => g.Kind).Should().Equal("unavailable", "not-supplied", "complete");
        view.Groups.Single(g => g.Kind == "unavailable").Lines.Single().Text.Should().Contain("runtime.locks").And.Contain("Not run: Lock Graph Analysis");
        view.Groups.Single(g => g.Kind == "not-supplied").Lines.Single().Text.Should().StartWith("No trace was supplied");
    }

    [Fact]
    public void Build_NamesAFailedAnalyzer_AndDoesNotCountItAsACapabilityGap()
    {
        CoverageRecord failed = new(null, null, CoverageStatus.Failed, [new CoverageImpact("Dominator Analysis", CoverageImpactKind.Failed)]);

        CoverageView view = CoverageProse.Build([Complete("heap.types"), failed])!;

        view.Headline.Should().Contain("1 of 1 capabilities complete").And.Contain("1 analyzer(s) failed (Dominator Analysis)");
        view.Groups.First().Kind.Should().Be("failed");
    }

    [Theory]
    [InlineData(null, null, "incoming-reference counting", "was skipped", "incoming-reference counting was skipped")]
    [InlineData(40L, null, "addresses", "have approximated counts", "40 addresses have approximated counts")]
    [InlineData(0L, 0L, "sampled types", "no valid sample", "no sampled types available (no valid sample)")]
    public void BoundaryText_ReadsAsOneSentenceFragment(long? affected, long? total, string unit, string limit, string expected)
    {
        CoverageRecord record = new(
            new Capability("x"), Dump, CoverageStatus.Partial, [], new CoverageBoundary(affected, total, unit, limit));

        CoverageProse.BoundaryText(record).Should().Be(expected);
    }

    private static CoverageRecord Complete(string capability) => new(new Capability(capability), Dump, CoverageStatus.Complete, []);
}
