namespace DumpDetective.Reporting.Models;

/// <summary>
/// Distribution of a numeric column, computed over the cells that are numbers: <see cref="Count"/> says how many that is, because a
/// column declared numeric can still hold a stray text cell and the stats must not pretend to cover it.
/// </summary>
internal readonly record struct ColumnStats(int Count, double Min, double P50, double P99, double Max, double Sum);

/// <summary>
/// One column of a <see cref="ColumnTable"/>. An unknown cell is stored as a null in <see cref="Values"/> and its reason lives beside it
/// (docs/ReportStructure/ReportSystemVision.md § 14.2): when every null in the column has the same reason it is stated once in
/// <see cref="DefaultNullReason"/>, otherwise each unknown cell's row is mapped in <see cref="NullReasons"/>. A null with no reason is a
/// plain null and appears in neither.
/// </summary>
internal sealed record ColumnTableColumn(
    string Name,
    string? Type,
    string? Format,
    bool Sortable,
    object?[] Values,
    UnknownReason? DefaultNullReason,
    IReadOnlyDictionary<int, UnknownReason>? NullReasons,
    ColumnStats? Stats);

/// <summary>A table stored column by column, so a viewer can sort, filter and aggregate one column without touching the others.</summary>
internal sealed record ColumnTable(string Title, int RowCount, IReadOnlyList<ColumnTableColumn> Columns);
