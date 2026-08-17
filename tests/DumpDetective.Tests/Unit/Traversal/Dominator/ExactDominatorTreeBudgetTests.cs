using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Traversal.Dominator;

/// <summary>
/// Pins the §D6 budget model and, more importantly, the sizing claims made for the shipped default.
/// The two reference-dump cases exist because this budget has already been wrong twice in opposite
/// directions (a 76 B/node constant that admitted graphs needing ~18GB, then a 220 B/node correction
/// that rejected a 58.34M-node graph already measured completing successfully). A regression in either
/// direction is silent at runtime — the analyzer just quietly falls back to the heuristic — so it needs
/// to fail here instead. See docs/analysis/phase1-redesigns/dominator-tree-memory-profile.md § 5.
/// </summary>
public class ExactDominatorTreeBudgetTests
{
    private const long GB = 1024L * 1024 * 1024;

    // Measured reachable-graph sizes from the two real dumps the design was validated against.
    private const long SmallDumpNodes = 6_686_490;
    private const long SmallDumpEdges = 17_367_740;
    private const long LargeDumpNodes = 58_339_936;
    private const long LargeDumpEdges = 137_030_000;

    private static ExactDominatorTreeBudget Default =>
        new(new RetentionOptions().ExactDominatorTreeMemoryBudgetBytes);

    [Fact]
    public void EstimateBytes_IsTheSumOfBothTerms()
    {
        var budget = new ExactDominatorTreeBudget(maxBytes: 1_000_000, bytesPerNode: 150, bytesPerEdge: 12);

        budget.EstimateBytes(nodeCount: 100, edgeCount: 200)
            .Should().Be((100 * 150) + (200 * 12));
    }

    [Fact]
    public void IsExceededBy_CanTripOnEdgesAloneWithNodesWellWithinBudget()
    {
        // The failure mode a flat node cap structurally cannot detect.
        var budget = new ExactDominatorTreeBudget(maxBytes: 10_000, bytesPerNode: 1, bytesPerEdge: 100);

        budget.IsExceededBy(nodeCount: 50, edgeCount: 10).Should().BeFalse();
        budget.IsExceededBy(nodeCount: 50, edgeCount: 200).Should().BeTrue("200 edges x 100 bytes exceeds 10,000 on its own");
    }

    [Fact]
    public void UnlimitedBudget_IsNeverExceeded()
    {
        ExactDominatorTreeBudget.Unlimited.IsEnforced.Should().BeFalse();
        ExactDominatorTreeBudget.Unlimited.IsExceededBy(long.MaxValue / 4, long.MaxValue / 4).Should().BeFalse();
    }

    [Fact]
    public void DefaultBudget_AdmitsTheSmallReferenceDump()
    {
        ExactDominatorTreeBudget budget = Default;

        budget.IsExceededBy(SmallDumpNodes, SmallDumpEdges).Should().BeFalse();
        budget.EstimateBytes(SmallDumpNodes, SmallDumpEdges)
            .Should().BeLessThan(2 * GB, "3.3GB dump projects ~1.13GB; real measured peak was 0.87GB");
    }

    [Fact]
    public void DefaultBudget_AdmitsTheLargeReferenceDumpWithComfortableMargin()
    {
        ExactDominatorTreeBudget budget = Default;

        budget.IsExceededBy(LargeDumpNodes, LargeDumpEdges)
            .Should().BeFalse("the 25.6GB dump's 58.34M-node graph was measured completing end-to-end; rejecting it is a regression");

        long projected = budget.EstimateBytes(LargeDumpNodes, LargeDumpEdges);
        projected.Should().BeInRange(9 * GB, 10 * GB, "projects ~9.68GB assuming zero leaf folding");
        // "Comfortable margin" is the actual requirement — barely fitting would mean the next slightly
        // larger dump silently drops to the heuristic.
        ((double)projected / budget.MaxBytes)
            .Should().BeLessThan(0.6, "should sit around half the budget, not scrape the ceiling");
    }

    [Fact]
    public void DefaultBudget_ScalesWellBeyondTheLargestMeasuredDump()
    {
        long admitted = Default.MaxNodesAtDensity(averageOutDegree: 2.5);

        admitted.Should().BeGreaterThan(2 * LargeDumpNodes,
            "the default must leave real headroom past the largest dump measured, not just barely cover it");
    }

    [Theory]
    [InlineData(2.35)] // 25.6GB dump
    [InlineData(2.60)] // 3.3GB dump
    public void MaxNodesAtDensity_IsFarStricterThanTheEdgelessFigure(double outDegree)
    {
        ExactDominatorTreeBudget budget = Default;

        // Guards against anyone reading MaxNodesIgnoringEdges as the real limit: at realistic densities
        // the edge term costs roughly as much again as the node term.
        budget.MaxNodesAtDensity(outDegree)
            .Should().BeLessThan((long)(budget.MaxNodesIgnoringEdges * 0.9));
    }

    [Fact]
    public void MaxNodesAtDensity_DecreasesAsGraphsGetDenser()
    {
        ExactDominatorTreeBudget budget = Default;

        budget.MaxNodesAtDensity(1.0)
            .Should().BeGreaterThan(budget.MaxNodesAtDensity(5.0));
    }

    [Fact]
    public void DefaultCoefficients_MatchTheDocumentedCostModel()
    {
        ExactDominatorTreeBudget budget = Default;

        budget.BytesPerNode.Should().Be(ExactDominatorTreeBudget.DefaultBytesPerNode);
        budget.BytesPerEdge.Should().Be(ExactDominatorTreeBudget.DefaultBytesPerEdge);
    }
}
