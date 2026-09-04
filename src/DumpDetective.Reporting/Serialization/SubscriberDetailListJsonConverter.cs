using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Serializes <c>EventLeakInstanceCard.SubscriberDetails</c>, whose elements are either an
/// inline <see cref="SubscriberDetailEntry"/> or an <see cref="int"/> index into the containing
/// section's <c>SubscriberDetailPool</c> (docs/refactor/report-payload-size-reduction-design.md,
/// F4). Unambiguous on the wire: a JSON number is always a pool index there, a JSON object is
/// always an inline entry.
/// </summary>
internal sealed class SubscriberDetailListJsonConverter : JsonConverter<IReadOnlyList<object>>
{
    public override IReadOnlyList<object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a JSON array for subscriber details.");

        var items = new List<object>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number)
                items.Add(reader.GetInt32());
            else
                items.Add(JsonSerializer.Deserialize<SubscriberDetailEntry>(ref reader, options)
                    ?? throw new JsonException("Expected a subscriber detail object."));
        }
        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<object> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (object item in value)
        {
            if (item is int index) writer.WriteNumberValue(index);
            else JsonSerializer.Serialize(writer, item, item.GetType(), options);
        }
        writer.WriteEndArray();
    }
}
