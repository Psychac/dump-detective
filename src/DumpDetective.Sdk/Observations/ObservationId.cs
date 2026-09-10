using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Observations;

/// <summary>Stable identifier for one <see cref="Observation"/>, used by <c>Finding.DerivedFrom</c>
/// and by trend/diff queries to re-identify the same observation across a temporal series.</summary>
/// <remarks>
/// Carries its own <see cref="JsonConverter"/> so it serializes as a bare JSON string in <c>"N"</c>
/// format (no hyphens), matching <see cref="ToString"/> exactly — until fixed 2026-09-10, this type
/// serialized as a wrapper object (<c>{"value":"..."}"</c>) carrying the <em>default</em> Guid
/// <c>"D"</c> format (hyphenated), silently disagreeing with what <c>ToString()</c> printed. See
/// docs/refactor/modularity/phase-1-sdk-review-findings.md items 8 and 9.
/// </remarks>
[JsonConverter(typeof(ObservationIdJsonConverter))]
public readonly record struct ObservationId(Guid Value)
{
    public static ObservationId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>Serializes <see cref="ObservationId"/> as a bare <c>"N"</c>-format JSON string — the
/// same format <see cref="ObservationId.ToString"/> uses — instead of the default one-key wrapper
/// object carrying the Guid's default <c>"D"</c> format.</summary>
public sealed class ObservationIdJsonConverter : JsonConverter<ObservationId>
{
    public override ObservationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(Guid.ParseExact(
            reader.GetString() ?? throw new JsonException("Expected a JSON string for ObservationId."),
            "N"));

    public override void Write(Utf8JsonWriter writer, ObservationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString("N"));
}
