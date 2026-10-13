using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Writes a <see cref="ColumnTable"/> as <c>{"title","rows","cols":[{"name","type","format","sortable","values","nullReason","nulls","stats"}]}</c>
/// (ReportSystemVision § 14.2). Cells go through <see cref="CompactCellJson"/>, so a cell has the same bytes here as in a row. An unknown cell is a
/// null in <c>values</c>: <c>nullReason</c> states a reason shared by every null in the column, otherwise <c>nulls</c> maps row to reason.
/// The viewer is the only reader, so there is no <c>Read</c>.
/// </summary>
internal sealed class ColumnTableJsonConverter : JsonConverter<ColumnTable>
{
    public override ColumnTable Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A ColumnTable is written for the viewer and is not read back in .NET.");

    public override void Write(Utf8JsonWriter writer, ColumnTable value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("title", value.Title);
        writer.WriteNumber("rows", value.RowCount);
        writer.WriteStartArray("cols");
        foreach (ColumnTableColumn column in value.Columns)
            WriteColumn(writer, column, options);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteColumn(Utf8JsonWriter writer, ColumnTableColumn column, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", column.Name);
        if (column.Type is not null)
            writer.WriteString("type", column.Type);
        if (column.Format is not null)
            writer.WriteString("format", column.Format);
        if (!column.Sortable)
            writer.WriteBoolean("sortable", false);

        writer.WriteStartArray("values");
        foreach (object? cell in column.Values)
            CompactCellJson.Write(writer, cell, options);
        writer.WriteEndArray();

        if (column.DefaultNullReason is { } defaultReason)
            writer.WriteString("nullReason", ReasonCode(defaultReason));

        if (column.NullReasons is { Count: > 0 } reasons)
        {
            writer.WriteStartObject("nulls");
            foreach (KeyValuePair<int, UnknownReason> reason in reasons.OrderBy(pair => pair.Key))
                writer.WriteString(reason.Key.ToString(CultureInfo.InvariantCulture), ReasonCode(reason.Value));
            writer.WriteEndObject();
        }

        if (column.Stats is { } stats)
        {
            writer.WriteStartObject("stats");
            writer.WriteNumber("count", stats.Count);
            writer.WriteNumber("min", stats.Min);
            writer.WriteNumber("p50", stats.P50);
            writer.WriteNumber("p99", stats.P99);
            writer.WriteNumber("max", stats.Max);
            writer.WriteNumber("sum", stats.Sum);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string ReasonCode(UnknownReason reason) => reason switch
    {
        UnknownReason.NotApplicable => "na",
        UnknownReason.Ambiguous => "amb",
        _ => "nm",
    };
}
