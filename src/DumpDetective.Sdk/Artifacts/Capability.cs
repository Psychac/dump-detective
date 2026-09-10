using System.Text.Json;
using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// A single capability an artifact can provide and an analyzer can require or optionally use —
/// e.g. "heap.objects", "trace.gc-events". A thin wrapper around the canonical string rather than
/// a bare <see cref="string"/> so capability values are distinguishable from arbitrary text at the
/// type level and can be validated against a vocabulary.
/// </summary>
/// <remarks>
/// The vocabulary is currently the constants in <see cref="CapabilityVocabulary"/>. Per
/// docs/refactor/modularity/phase-1-contracts-sdk.md, the long-term source of truth is a checked-in,
/// versioned <c>capability-registry.json</c> validated at build time — deferred until Phase 3/6b
/// have real capability declarations to validate against (see
/// docs/refactor/modularity-plan.md § 10 point 4). <see cref="CapabilityVocabulary"/> is the
/// interim source of truth until that registry lands.
///
/// Carries its own <see cref="JsonConverter"/> so it serializes as a bare JSON string
/// (<c>"heap.objects"</c>), not a one-key wrapper object — see
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 8.
/// </remarks>
[JsonConverter(typeof(CapabilityJsonConverter))]
public readonly record struct Capability(string Key)
{
    public static implicit operator Capability(string key) => new(key);

    public override string ToString() => Key;
}

/// <summary>Serializes <see cref="Capability"/> as a bare JSON string instead of the default
/// one-key wrapper object System.Text.Json would otherwise produce for a single-property record.</summary>
public sealed class CapabilityJsonConverter : JsonConverter<Capability>
{
    public override Capability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Expected a JSON string for Capability."));

    public override void Write(Utf8JsonWriter writer, Capability value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Key);
}
