using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Reporting.Models;

/// <summary>
/// Which entities appear together in the report (docs/ReportStructure/m2b-relations-plan.md, step 1). Each pair is stored once, lower
/// entity id first, and the client reads it in both directions. <paramref name="Sources"/> names where the pair was seen
/// ("analyzer / table title") and each pair lists indexes into it. "Appears with" is all this says: two entities in one table row,
/// not that one holds or retains the other.
/// </summary>
internal sealed record EntityRelations(IReadOnlyList<string> Sources, IReadOnlyList<EntityRelation> Pairs);

/// <summary>Written as <c>[firstId, secondId, [sourceIndex, ...]]</c>.</summary>
[JsonConverter(typeof(EntityRelationJsonConverter))]
internal sealed record EntityRelation(int First, int Second, IReadOnlyList<int> SourceIndexes);

internal sealed class EntityRelationJsonConverter : JsonConverter<EntityRelation>
{
    public override EntityRelation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("EntityRelation is written for the report client and is never read back by the tool.");

    // One raw line per pair for the same reason as EntityFacts: an indented writer would put every number of 13,000 pairs on its own line.
    public override void Write(Utf8JsonWriter writer, EntityRelation value, JsonSerializerOptions options)
    {
        var json = new StringBuilder(24);
        json.Append('[').Append(value.First).Append(',').Append(value.Second).Append(",[").AppendJoin(',', value.SourceIndexes).Append("]]");
        writer.WriteRawValue(json.ToString(), skipInputValidation: true);
    }
}
