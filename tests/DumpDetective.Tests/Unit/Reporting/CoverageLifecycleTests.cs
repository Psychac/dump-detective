using DumpDetective.Core.Models;
using DumpDetective.Reporting.Trend;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Synthesis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class CoverageLifecycleTests
{
    private static readonly ArtifactId Dump = new("d");

    private static CoverageRecord Partial(string capability, string analyzer, long affected = 8) => new(
        new Capability(capability), Dump, CoverageStatus.Partial, [new CoverageImpact(analyzer, CoverageImpactKind.Limited)],
        new CoverageBoundary(affected, 10, "types", "hit the budget"));

    private static CoverageRecord Failed(string analyzer) =>
        new(null, null, CoverageStatus.Failed, [new CoverageImpact(analyzer, CoverageImpactKind.Failed)]);

    private static AnalysisSnapshot Snapshot(int index, IReadOnlyList<Claim> findings, params CoverageRecord[] coverage) => new(
        index, $"d{index}.dmp", [], findings, new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow, Coverage: coverage);

    private static Claim Finding(string analyzer, string fingerprint) =>
        ClaimFactory.Create(analyzer, "Cat", Severity.Warning, "title", "evidence", "rec", [], fingerprint);

    [Fact]
    public void ALimitInOnlyOneDump_MakesTheAnalyzerNotComparable_AndIsReportedAsAChange()
    {
        CoverageLifecycleResult result = CoverageLifecycleComparer.Compare(
            Snapshot(0, []), Snapshot(1, [], Partial("heap.root-paths", "Reference Chain Analysis")));

        result.NotComparableAnalyzers.Should().BeEquivalentTo("Reference Chain Analysis");
        result.Changes.Should().ContainSingle().Which.Text.Should().Contain("heap.root-paths partial in the last dump, not in the first");
    }

    [Fact]
    public void TheSameLimitInBothDumps_IsComparable_EvenWhenItsSizeDiffers()
    {
        CoverageLifecycleResult result = CoverageLifecycleComparer.Compare(
            Snapshot(0, [], Partial("heap.root-paths", "Reference Chain Analysis", affected: 8)),
            Snapshot(1, [], Partial("heap.root-paths", "Reference Chain Analysis", affected: 3)));

        result.NotComparableAnalyzers.Should().BeEmpty();
        result.Changes.Should().BeEmpty();
    }

    [Fact]
    public void AnAnalyzerThatFailedInOneDump_IsNotComparable()
    {
        CoverageLifecycleComparer.Compare(Snapshot(0, []), Snapshot(1, [], Failed("Dominator Analysis")))
            .NotComparableAnalyzers.Should().BeEquivalentTo("Dominator Analysis");
    }

    [Fact]
    public void NoCoverageOnEitherSide_ChangesNothing()
    {
        var plain = new AnalysisSnapshot(0, "d", [], [], new Dictionary<string, AnalyzerDomainResult>(), DateTime.UtcNow);

        CoverageLifecycleComparer.Compare(plain, plain).NotComparableAnalyzers.Should().BeEmpty();
    }

    [Fact]
    public void Lifecycle_ANewOrResolvedFindingOfADifferentlyCoveredAnalyzer_IsNotComparable_NotNewOrResolved()
    {
        Claim resolvedLooking = Finding("Dominator Analysis", "dominator::top");
        Claim newLooking = Finding("Reference Chain Analysis", "chain::new");
        Claim steady = Finding("Reference Chain Analysis", "chain::steady");
        Claim trulyNew = Finding("Thread Analysis", "thread::new");

        FindingLifecycleResult result = FindingLifecycleComparer.Compare(
            Snapshot(0, [resolvedLooking, steady]),
            Snapshot(1, [newLooking, steady, trulyNew], Failed("Dominator Analysis"), Partial("heap.root-paths", "Reference Chain Analysis")));

        result.NotComparable.Select(f => f.Fingerprint).Should().BeEquivalentTo("dominator::top", "chain::new");
        result.ResolvedFindings.Should().BeEmpty();
        result.NewFindings.Select(f => f.Fingerprint).Should().Equal("thread::new");
        result.PersistentFindings.Select(f => f.Fingerprint).Should().Equal("chain::steady");
    }
}
