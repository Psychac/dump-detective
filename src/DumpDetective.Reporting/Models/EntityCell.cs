using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Sdk.Identity;

namespace DumpDetective.Reporting.Models;

internal static class EntityColumn
{
    public const string TypePrefix = "entity:";
    public const string Type = TypePrefix + "type";
    public const string Thread = TypePrefix + "thread";
    public const string Module = TypePrefix + "module";
    public const string Method = TypePrefix + "method";

    // A method is keyed "<declaring type>::<name>" but its column shows only the name, so the declaring type comes from another
    // column of the same row: the entity:type column with this header.
    public const string MethodDeclaringTypeHeader = "Declaring Type";

    public static bool IsEntityColumn(CompactHeader header) =>
        header.Type is not null && header.Type.StartsWith(TypePrefix, StringComparison.Ordinal);

    public static EntityKind KindOf(CompactHeader header) => header.Type switch
    {
        Type => EntityKind.Type,
        Thread => EntityKind.Thread,
        Module => EntityKind.Module,
        Method => EntityKind.Method,
        _ => throw new ArgumentException($"Not an entity column: {header.Type}", nameof(header)),
    };
}

/// <summary>
/// A compact-table cell that refers to an entity-table row. <paramref name="ExactName"/> is set only when the table
/// showed a name the entity does not carry (the entity table merges compiler-generated names such as
/// <c>DisplayClass132_0</c>), so the exact text survives. Written as the bare id, or <c>[id, "exact name"]</c>.
/// </summary>
[JsonConverter(typeof(EntityCellJsonConverter))]
internal sealed record EntityCell(int Id, string? ExactName);

internal sealed class EntityCellJsonConverter : JsonConverter<EntityCell>
{
    public override EntityCell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new EntityCell(reader.GetInt32(), null);

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a number or [id, name] for an entity cell.");

        reader.Read();
        int id = reader.GetInt32();
        reader.Read();
        string? exactName = reader.GetString();
        reader.Read();
        return new EntityCell(id, exactName);
    }

    public override void Write(Utf8JsonWriter writer, EntityCell value, JsonSerializerOptions options)
    {
        if (value.ExactName is null)
        {
            writer.WriteNumberValue(value.Id);
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(value.Id);
        writer.WriteStringValue(value.ExactName);
        writer.WriteEndArray();
    }
}
