using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Serializes <see cref="CompactRow"/> as a bare JSON array instead of {"values":[...]} —
/// 11 bytes of pure wrapper syntax per row, ~1.8 MB across a large report's ~175k rows
/// (docs/refactor/report-payload-size-reduction-design.md, F2). The client already accepts
/// this shape.
/// </summary>
internal sealed class CompactRowJsonConverter : JsonConverter<CompactRow>
{
    public override CompactRow Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array for CompactRow.");

        var values = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out long l) ? l : reader.GetDouble(),
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                JsonTokenType.StartObject => ReadObjectCell(ref reader, options),
                _ => throw new JsonException($"Unexpected token {reader.TokenType} in CompactRow."),
            });
        }
        return new CompactRow(values.ToArray());
    }

    // An object cell is either an unknown-cell marker ({"u":...}) or a text-and-sort-value record ({"display":...}).
    private static object? ReadObjectCell(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        using JsonDocument cell = JsonDocument.ParseValue(ref reader);
        return cell.RootElement.TryGetProperty("u", out _)
            ? cell.RootElement.Deserialize<DumpDetective.Reporting.Models.UnknownCell>(options)
            : cell.RootElement.Deserialize<DumpDetective.Reporting.Models.TableCell>(options);
    }

    public override void Write(Utf8JsonWriter writer, CompactRow value, JsonSerializerOptions options)
    {
        // An indented writer would put every cell on its own line (and an unknown cell on three); a row is written as one line instead.
        Utf8JsonWriter target = options.WriteIndented ? InlineJson.Begin(options, elementOf: writer) : writer;
        target.WriteStartArray();
        foreach (object? cell in value.Values)
            CompactCellJson.Write(target, cell, options);
        target.WriteEndArray();
        if (options.WriteIndented)
            InlineJson.End(writer);
    }
}

/// <summary>How one compact-table cell is written, shared by row-major <see cref="CompactRow"/>s and column-major <see cref="ColumnTable"/>s so both carry the same bytes for the same cell.</summary>
internal static class CompactCellJson
{
    public static void Write(Utf8JsonWriter writer, object? cell, JsonSerializerOptions options)
    {
        switch (cell)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case long l: writer.WriteNumberValue(l); break;
            case ulong ul: writer.WriteNumberValue(ul); break;
            // Rounded to 3 decimal places — these are display percentages/ratios, not exact
            // measurements, and full double precision (e.g. 83.11189368948767) is wasted
            // wire bytes (docs/refactor/report-payload-size-reduction-design.md, F6).
            case double d: writer.WriteNumberValue(Math.Round(d, 3)); break;
            case float f: writer.WriteNumberValue(MathF.Round(f, 3)); break;
            case short sh: writer.WriteNumberValue(sh); break;
            default: JsonSerializer.Serialize(writer, cell, cell.GetType(), options); break;
        }
    }
}
