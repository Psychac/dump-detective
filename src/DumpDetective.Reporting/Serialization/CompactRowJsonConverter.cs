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
                _ => throw new JsonException($"Unexpected token {reader.TokenType} in CompactRow."),
            });
        }
        return new CompactRow(values.ToArray());
    }

    public override void Write(Utf8JsonWriter writer, CompactRow value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (object? cell in value.Values)
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
        writer.WriteEndArray();
    }
}
