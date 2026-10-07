using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Reporting.Models;

/// <summary>
/// What the report knows about each entity, grouped by the kind of observation that named it, small enough to embed
/// (docs/ReportStructure/m2-entities-plan.md, step 5). Fact-kind and measure names are stored once in the two lists and
/// referenced by index from every entity, in sorted order so identical input produces identical output.
/// </summary>
internal sealed record EntitySummary(
    IReadOnlyList<FactKindRecord> FactKinds,
    IReadOnlyList<MeasureRecord> Measures,
    IReadOnlyList<EntityFacts> Entities);

internal sealed record FactKindRecord(string Name, string Analyzer, string Panel);

internal sealed record MeasureRecord(string Name, string Unit);

/// <summary>Written as <c>[entityId, [[factKind, count, [[measure, sum, min, max], ...]], ...]]</c>.</summary>
[JsonConverter(typeof(EntityFactsJsonConverter))]
internal sealed record EntityFacts(int EntityId, IReadOnlyList<FactGroup> Groups);

internal sealed record FactGroup(int FactKind, int Count, IReadOnlyList<MeasureStat> Stats);

/// <summary>
/// <paramref name="Sum"/> is null when adding the values would be wrong (retained bytes overlap, averages and ratios do not add):
/// only <paramref name="Min"/> and <paramref name="Max"/> are meaningful then. With a single fact all three are its value.
/// </summary>
internal sealed record MeasureStat(int Measure, double? Sum, double Min, double Max);

internal sealed class EntityFactsJsonConverter : JsonConverter<EntityFacts>
{
    public override EntityFacts Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("EntityFacts is written for the report client and is never read back by the tool.");

    // Written as one raw line per entity: report.json is indented, and indenting nested arrays puts every number on its own
    // line (+19 MB on the reference dump for what is 2.2 MB compact).
    public override void Write(Utf8JsonWriter writer, EntityFacts value, JsonSerializerOptions options)
    {
        var json = new StringBuilder(64);
        json.Append('[').Append(value.EntityId).Append(",[");
        for (int g = 0; g < value.Groups.Count; g++)
        {
            FactGroup group = value.Groups[g];
            if (g > 0) json.Append(',');
            json.Append('[').Append(group.FactKind).Append(',').Append(group.Count).Append(",[");
            for (int s = 0; s < group.Stats.Count; s++)
            {
                MeasureStat stat = group.Stats[s];
                if (s > 0) json.Append(',');
                json.Append('[').Append(stat.Measure).Append(',');
                if (stat.Sum is double sum)
                    json.Append(Number(sum));
                else
                    json.Append("null");
                json.Append(',').Append(Number(stat.Min)).Append(',').Append(Number(stat.Max)).Append(']');
            }

            json.Append("]]");
        }

        json.Append("]]");
        writer.WriteRawValue(json.ToString(), skipInputValidation: true);
    }

    private static string Number(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);
}
