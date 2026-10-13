using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class LeakCandidateUnknownCellTests
{
    private static LeakCandidateRecord Candidate(string type, RootChainSearch search, string? chain = null, bool staticRooted = false) => new(
        type, TotalSize: 1_000, InstanceCount: 1, Gen2Pct: 100, IsFinalizable: false, IsContainer: false, ReferenceFieldRatio: 0,
        IsStaticRooted: staticRooted, RootChain: chain, RootChainSearch: search);

    private static CompactTable Table(params LeakCandidateRecord[] candidates) =>
        new LeakCandidateSectionBuilder().Build(new LeakCandidateDomainResult(candidates.Length, candidates, LogicalTimerCount: 0))
            .CompactTables!.Single(t => t.Title == "Top leak candidates by suspicion score");

    private static object? Cell(CompactTable table, int row, string column) =>
        table.Rows[row].Values[table.Headers.ToList().FindIndex(h => h.Name == column)];

    [Fact]
    public void RootChain_EachSearchOutcomeShowsAsItsOwnKindOfCell()
    {
        CompactTable table = Table(
            Candidate("A", RootChainSearch.Found, chain: "StaticVar -> A@0x1"),
            Candidate("B", RootChainSearch.Unreachable),
            Candidate("C", RootChainSearch.SearchTruncated),
            Candidate("D", RootChainSearch.NotSearched));

        Cell(table, 0, "Root Chain").Should().Be("StaticVar -> A@0x1");
        Cell(table, 1, "Root Chain").Should().Be("No root path", "a confirmed result is a value, not a gap");
        Cell(table, 2, "Root Chain").Should().Be(new UnknownCell(UnknownReason.Ambiguous));
        Cell(table, 3, "Root Chain").Should().Be(new UnknownCell(UnknownReason.NotMeasured));
    }

    [Fact]
    public void ClassAndRootKind_AreAmbiguousWhenTheClassifierCouldNotDecide_AndTextOtherwise()
    {
        CompactTable table = Table(
            Candidate("Plain", RootChainSearch.NotSearched),
            Candidate("Static", RootChainSearch.NotSearched, staticRooted: true));

        Cell(table, 0, "Class").Should().Be(new UnknownCell(UnknownReason.Ambiguous));
        Cell(table, 0, "Root Kind").Should().Be(new UnknownCell(UnknownReason.Ambiguous));
        Cell(table, 1, "Class").Should().BeOfType<string>().Which.Should().NotBe("Unknown");
        Cell(table, 1, "Root Kind").Should().BeOfType<string>();
    }
}
