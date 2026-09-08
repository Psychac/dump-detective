using System.Linq;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class DbConnectionAnalyzerRootPathSelectionTests
{
    [Fact]
    public void SelectForRootPathEnrichment_FiltersToGen2Only()
    {
        var snapshots = new List<DbConnectionSnapshot>
        {
            Snap(0x1, generation: 0, retainedBytes: 500),
            Snap(0x2, generation: 1, retainedBytes: 500),
            Snap(0x3, generation: 2, retainedBytes: 500),
        };

        var selected = DbConnectionAnalyzer.SelectForRootPathEnrichment(snapshots, cap: 20);

        selected.Should().ContainSingle();
        selected[0].Address.Should().Be(0x3);
    }

    [Fact]
    public void SelectForRootPathEnrichment_OrdersByRetainedBytesDescending()
    {
        var snapshots = new List<DbConnectionSnapshot>
        {
            Snap(0x1, generation: 2, retainedBytes: 100),
            Snap(0x2, generation: 2, retainedBytes: 900),
            Snap(0x3, generation: 2, retainedBytes: 500),
        };

        var selected = DbConnectionAnalyzer.SelectForRootPathEnrichment(snapshots, cap: 20);

        selected.Select(s => s.Address).Should().Equal(0x2UL, 0x3UL, 0x1UL);
    }

    [Fact]
    public void SelectForRootPathEnrichment_UnknownRetainedBytesSortsLast()
    {
        var snapshots = new List<DbConnectionSnapshot>
        {
            Snap(0x1, generation: 2, retainedBytes: null),
            Snap(0x2, generation: 2, retainedBytes: 10),
        };

        var selected = DbConnectionAnalyzer.SelectForRootPathEnrichment(snapshots, cap: 20);

        selected.Select(s => s.Address).Should().Equal(0x2UL, 0x1UL);
    }

    [Fact]
    public void SelectForRootPathEnrichment_CapsAtLimit()
    {
        var snapshots = new List<DbConnectionSnapshot>();
        for (int i = 1; i <= 30; i++)
            snapshots.Add(Snap((ulong)i, generation: 2, retainedBytes: (ulong)i));

        var selected = DbConnectionAnalyzer.SelectForRootPathEnrichment(snapshots, cap: 20);

        selected.Should().HaveCount(20);
        // Highest 20 by retained bytes: addresses 11..30.
        selected.Select(s => s.Address).Should().OnlyContain(a => a >= 11);
    }

    [Fact]
    public void SelectForRootPathEnrichment_ReturnsEmpty_WhenNoGen2Connections()
    {
        var snapshots = new List<DbConnectionSnapshot>
        {
            Snap(0x1, generation: 0, retainedBytes: 500),
            Snap(0x2, generation: 1, retainedBytes: 500),
        };

        DbConnectionAnalyzer.SelectForRootPathEnrichment(snapshots, cap: 20).Should().BeEmpty();
    }

    private static DbConnectionSnapshot Snap(ulong address, sbyte generation, ulong? retainedBytes) =>
        new("Microsoft.Data.SqlClient.SqlConnection", address, "Open", 1, null, generation, retainedBytes);
}
