using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Turns a row-major <see cref="CompactTable"/> into a <see cref="ColumnTable"/> and back. It is a pure reshape: cells keep whatever
/// form earlier passes gave them (string-pool indexes, <see cref="EntityCell"/>s), so it runs after <c>EntityCellPass</c> and
/// <c>ReportStringPool</c> and re-encodes nothing.
/// </summary>
internal static class ColumnTableConverter
{
    private static readonly HashSet<string> NumericHeaderTypes = new(StringComparer.Ordinal)
    {
        "number", "bytes", "percent", "int", "long", "double",
    };

    public static ColumnTable ToColumns(CompactTable table)
    {
        int rowCount = table.Rows.Count;
        var columns = new List<ColumnTableColumn>(table.Headers.Count);
        for (int columnIndex = 0; columnIndex < table.Headers.Count; columnIndex++)
            columns.Add(BuildColumn(table, columnIndex, rowCount));

        return new ColumnTable(table.Title, rowCount, columns);
    }

    public static CompactTable ToRows(ColumnTable table, int rowLimit = 20)
    {
        var headers = new List<CompactHeader>(table.Columns.Count);
        foreach (ColumnTableColumn column in table.Columns)
            headers.Add(new CompactHeader(column.Name, column.Type, column.Format, column.Sortable));

        var rows = new List<CompactRow>(table.RowCount);
        for (int row = 0; row < table.RowCount; row++)
        {
            var cells = new object?[table.Columns.Count];
            for (int columnIndex = 0; columnIndex < cells.Length; columnIndex++)
                cells[columnIndex] = CellAt(table.Columns[columnIndex], row);

            rows.Add(new CompactRow(cells));
        }

        return new CompactTable(table.Title, headers, rows, rowLimit);
    }

    private static ColumnTableColumn BuildColumn(CompactTable table, int columnIndex, int rowCount)
    {
        CompactHeader header = table.Headers[columnIndex];
        var values = new object?[rowCount];
        List<(int Row, UnknownReason Reason)>? unknownCells = null;
        bool plainNullSeen = false;

        for (int row = 0; row < rowCount; row++)
        {
            object?[] source = table.Rows[row].Values;
            if (source.Length > table.Headers.Count)
                throw new InvalidOperationException(
                    $"Row {row} of table '{table.Title}' has {source.Length} cells for {table.Headers.Count} headers; a column store would drop the extra cells.");

            object? cell = columnIndex < source.Length ? source[columnIndex] : null;
            if (cell is UnknownCell unknown)
            {
                (unknownCells ??= []).Add((row, unknown.Reason));
                continue;
            }

            values[row] = cell;
            plainNullSeen |= cell is null;
        }

        (UnknownReason? defaultReason, IReadOnlyDictionary<int, UnknownReason>? reasons) = SummarizeUnknownCells(unknownCells, plainNullSeen);
        ColumnStats? stats = header.Type is not null && NumericHeaderTypes.Contains(header.Type) ? ComputeStats(values) : null;
        return new ColumnTableColumn(header.Name, header.Type, header.Format, header.Sortable, values, defaultReason, reasons, stats);
    }

    private static (UnknownReason? DefaultReason, IReadOnlyDictionary<int, UnknownReason>? Reasons) SummarizeUnknownCells(
        List<(int Row, UnknownReason Reason)>? unknownCells, bool plainNullSeen)
    {
        if (unknownCells is null)
            return (null, null);

        UnknownReason first = unknownCells[0].Reason;
        bool singleReason = true;
        foreach ((int _, UnknownReason reason) in unknownCells)
            singleReason &= reason == first;

        // A column-wide default would also describe a plain null, so it is only stated when no plain null shares the column.
        if (singleReason && !plainNullSeen)
            return (first, null);

        var reasons = new Dictionary<int, UnknownReason>(unknownCells.Count);
        foreach ((int row, UnknownReason reason) in unknownCells)
            reasons[row] = reason;

        return (null, reasons);
    }

    private static object? CellAt(ColumnTableColumn column, int row)
    {
        object? value = column.Values[row];
        if (value is not null)
            return value;

        if (column.NullReasons is not null && column.NullReasons.TryGetValue(row, out UnknownReason reason))
            return new UnknownCell(reason);

        return column.DefaultNullReason is { } defaultReason ? new UnknownCell(defaultReason) : null;
    }

    private static ColumnStats? ComputeStats(object?[] values)
    {
        var numbers = new double[values.Length];
        int count = 0;
        double sum = 0;
        foreach (object? value in values)
        {
            if (!TryGetNumber(value, out double number))
                continue;

            numbers[count++] = number;
            sum += number;
        }

        if (count == 0)
            return null;

        Array.Sort(numbers, 0, count);
        return new ColumnStats(count, numbers[0], NearestRank(numbers, count, 0.50), NearestRank(numbers, count, 0.99), numbers[count - 1], sum);
    }

    private static double NearestRank(double[] sorted, int count, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * count);
        return sorted[Math.Clamp(rank - 1, 0, count - 1)];
    }

    private static bool TryGetNumber(object? value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case ulong ul: number = ul; return true;
            case double d: number = d; return true;
            case float f: number = f; return true;
            case short s: number = s; return true;
            default: number = 0; return false;
        }
    }
}
