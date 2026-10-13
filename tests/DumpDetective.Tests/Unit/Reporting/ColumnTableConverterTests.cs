using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ColumnTableConverterTests
{
    private static CompactTable Table(CompactHeader[] headers, params object?[][] rows) =>
        new("Sample", headers, rows.Select(values => new CompactRow(values)).ToArray());

    [Fact]
    public void ToColumns_StoresEachColumnSeparately_AndKeepsHeaderMetadata()
    {
        CompactTable table = Table(
            [new CompactHeader("Name", "string"), new CompactHeader("Size", "bytes", "bytes", Sortable: false)],
            ["a", 10L],
            ["b", 20L]);

        ColumnTable columns = ColumnTableConverter.ToColumns(table);

        columns.RowCount.Should().Be(2);
        columns.Columns.Select(column => column.Name).Should().Equal("Name", "Size");
        columns.Columns[0].Values.Should().Equal("a", "b");
        columns.Columns[1].Values.Should().Equal(10L, 20L);
        columns.Columns[1].Format.Should().Be("bytes");
        columns.Columns[1].Sortable.Should().BeFalse();
    }

    [Fact]
    public void ToColumns_UnknownCellsAllSharingOneReason_StateTheReasonOnceForTheColumn()
    {
        CompactTable table = Table(
            [new CompactHeader("Disposed")],
            [new UnknownCell(UnknownReason.NotApplicable)],
            [new UnknownCell(UnknownReason.NotApplicable)]);

        ColumnTableColumn column = ColumnTableConverter.ToColumns(table).Columns[0];

        column.Values.Should().OnlyContain(value => value == null).And.HaveCount(2);
        column.DefaultNullReason.Should().Be(UnknownReason.NotApplicable);
        column.NullReasons.Should().BeNull();
    }

    [Fact]
    public void ToColumns_UnknownCellsWithDifferentReasons_MapEachRow()
    {
        CompactTable table = Table(
            [new CompactHeader("Disposed")],
            ["Yes"],
            [new UnknownCell(UnknownReason.NotApplicable)],
            [new UnknownCell(UnknownReason.Ambiguous)]);

        ColumnTableColumn column = ColumnTableConverter.ToColumns(table).Columns[0];

        column.DefaultNullReason.Should().BeNull();
        column.NullReasons.Should().BeEquivalentTo(new Dictionary<int, UnknownReason>
        {
            [1] = UnknownReason.NotApplicable,
            [2] = UnknownReason.Ambiguous,
        });
    }

    [Fact]
    public void ToColumns_PlainNullBesideUnknownCells_IsNotGivenTheColumnDefaultReason()
    {
        CompactTable table = Table(
            [new CompactHeader("Retained", "bytes")],
            [null],
            [new UnknownCell(UnknownReason.NotMeasured)]);

        ColumnTableColumn column = ColumnTableConverter.ToColumns(table).Columns[0];

        column.DefaultNullReason.Should().BeNull();
        column.NullReasons.Should().BeEquivalentTo(new Dictionary<int, UnknownReason> { [1] = UnknownReason.NotMeasured });
    }

    [Fact]
    public void ToColumns_ColumnWithoutUnknownCells_HasNoReasons()
    {
        CompactTable table = Table([new CompactHeader("Name")], ["a"], [null]);

        ColumnTableColumn column = ColumnTableConverter.ToColumns(table).Columns[0];

        column.DefaultNullReason.Should().BeNull();
        column.NullReasons.Should().BeNull();
    }

    [Fact]
    public void ToColumns_NumericColumn_ComputesStatsOverItsNumericCells()
    {
        CompactTable table = Table(
            [new CompactHeader("Size", "bytes")],
            [5L], [1L], [3L], [4L], [2L]);

        ColumnStats stats = ColumnTableConverter.ToColumns(table).Columns[0].Stats!.Value;

        stats.Should().Be(new ColumnStats(Count: 5, Min: 1, P50: 3, P99: 5, Max: 5, Sum: 15));
    }

    [Fact]
    public void ToColumns_NumericColumnWithStrayTextAndUnknownCells_CountsOnlyTheNumbers()
    {
        CompactTable table = Table(
            [new CompactHeader("Size", "number")],
            [10L], ["N/A"], [new UnknownCell(UnknownReason.NotMeasured)], [30L]);

        ColumnStats stats = ColumnTableConverter.ToColumns(table).Columns[0].Stats!.Value;

        stats.Count.Should().Be(2);
        stats.Sum.Should().Be(40);
    }

    [Fact]
    public void ToColumns_StringColumnHoldingPoolIndexes_HasNoStats()
    {
        CompactTable table = Table([new CompactHeader("Generation", "string")], [7], [7], [9]);

        ColumnTableConverter.ToColumns(table).Columns[0].Stats.Should().BeNull();
    }

    [Fact]
    public void ToColumns_NumericColumnWithNoNumbers_HasNoStats()
    {
        CompactTable table = Table([new CompactHeader("Size", "bytes")], [null], ["N/A"]);

        ColumnTableConverter.ToColumns(table).Columns[0].Stats.Should().BeNull();
    }

    [Fact]
    public void ToColumns_EntityCells_AreCarriedUnchanged()
    {
        var cell = new EntityCell(4, "Exact.Name");
        CompactTable table = Table([new CompactHeader("Type", EntityColumn.Type)], [cell]);

        ColumnTableConverter.ToColumns(table).Columns[0].Values[0].Should().BeSameAs(cell);
    }

    [Fact]
    public void ToColumns_ShortRow_ReadsMissingCellsAsPlainNulls()
    {
        CompactTable table = Table([new CompactHeader("A"), new CompactHeader("B")], ["x"]);

        ColumnTableConverter.ToColumns(table).Columns[1].Values.Should().Equal((object?)null);
    }

    [Fact]
    public void ToColumns_RowWithMoreCellsThanHeaders_Throws()
    {
        CompactTable table = Table([new CompactHeader("A")], ["x", "extra"]);

        Action convert = () => ColumnTableConverter.ToColumns(table);

        convert.Should().Throw<InvalidOperationException>().WithMessage("*Row 0*Sample*");
    }

    [Fact]
    public void ToColumns_EmptyTable_KeepsTheColumns()
    {
        ColumnTable columns = ColumnTableConverter.ToColumns(Table([new CompactHeader("Size", "bytes")]));

        columns.RowCount.Should().Be(0);
        columns.Columns.Should().ContainSingle();
        columns.Columns[0].Values.Should().BeEmpty();
        columns.Columns[0].Stats.Should().BeNull();
    }

    [Fact]
    public void ToRows_RoundTripsEveryKindOfCell()
    {
        var entity = new EntityCell(2, null);
        CompactTable original = Table(
            [new CompactHeader("Type", EntityColumn.Type), new CompactHeader("Size", "bytes"), new CompactHeader("Flag"), new CompactHeader("Note")],
            [entity, 10L, new UnknownCell(UnknownReason.NotApplicable), "a"],
            ["plain", null, new UnknownCell(UnknownReason.Ambiguous), null],
            [entity, 30L, "Yes", 7]);

        CompactTable roundTripped = ColumnTableConverter.ToRows(ColumnTableConverter.ToColumns(original));

        roundTripped.Title.Should().Be(original.Title);
        roundTripped.Headers.Should().Equal(original.Headers);
        roundTripped.Rows.Select(row => row.Values).Should().BeEquivalentTo(original.Rows.Select(row => row.Values), options => options.WithStrictOrdering());
    }

    [Fact]
    public void ToRows_ColumnWithOneSharedReason_RestoresUnknownCellsNotPlainNulls()
    {
        CompactTable original = Table(
            [new CompactHeader("Disposed")],
            [new UnknownCell(UnknownReason.NotApplicable)],
            [new UnknownCell(UnknownReason.NotApplicable)]);

        CompactTable roundTripped = ColumnTableConverter.ToRows(ColumnTableConverter.ToColumns(original));

        roundTripped.Rows.Select(row => row.Values[0]).Should().Equal(new UnknownCell(UnknownReason.NotApplicable), new UnknownCell(UnknownReason.NotApplicable));
    }
}
