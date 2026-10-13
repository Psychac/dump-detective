using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Reporting.Models;

/// <summary>Why a table cell has no value (docs/ReportStructure/ReportSystemVision.md § 7.3). Zero is a value and never uses this.</summary>
internal enum UnknownReason
{
    /// <summary>The analysis did not measure it (a bounded walk that stopped short, or a capability that was missing).</summary>
    NotMeasured,

    /// <summary>The question does not apply to this row, for example a disposed flag on a type that is not disposable.</summary>
    NotApplicable,

    /// <summary>It was looked for and the result cannot decide the answer.</summary>
    Ambiguous,
}

internal static class UnknownReasonText
{
    public const string NotMeasuredMark = "—";
    public const string NotApplicableMark = "n/a";
    public const string AmbiguousMark = "?";

    public static string Mark(UnknownReason reason) => reason switch
    {
        UnknownReason.NotApplicable => NotApplicableMark,
        UnknownReason.Ambiguous => AmbiguousMark,
        _ => NotMeasuredMark,
    };
}

/// <summary>
/// A compact-table cell with no value, and the reason. Written as <c>{"u":"nm"}</c> (not measured), <c>{"u":"na"}</c> (not applicable) or
/// <c>{"u":"amb"}</c> (ambiguous): an object is the only JSON shape no value cell uses, so it cannot be mistaken for a string-pool index
/// or an entity id, and the short codes keep tens of thousands of cells small. <see cref="ToString"/> gives the mark a text renderer prints.
/// </summary>
[JsonConverter(typeof(UnknownCellJsonConverter))]
internal sealed record UnknownCell(UnknownReason Reason)
{
    public override string ToString() => UnknownReasonText.Mark(Reason);
}

internal sealed class UnknownCellJsonConverter : JsonConverter<UnknownCell>
{
    private const string Property = "u";

    public override UnknownCell Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected an object for an unknown cell.");

        reader.Read();
        if (reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != Property)
            throw new JsonException("Expected the \"u\" property in an unknown cell.");

        reader.Read();
        UnknownReason reason = reader.GetString() switch
        {
            "nm" => UnknownReason.NotMeasured,
            "na" => UnknownReason.NotApplicable,
            "amb" => UnknownReason.Ambiguous,
            var other => throw new JsonException($"Unknown cell reason '{other}'."),
        };
        reader.Read();
        return new UnknownCell(reason);
    }

    public override void Write(Utf8JsonWriter writer, UnknownCell value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(Property, value.Reason switch
        {
            UnknownReason.NotApplicable => "na",
            UnknownReason.Ambiguous => "amb",
            _ => "nm",
        });
        writer.WriteEndObject();
    }
}
