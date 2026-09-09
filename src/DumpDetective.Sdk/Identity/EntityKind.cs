using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Identity;

/// <summary>Discriminates the concrete <see cref="EntityRef"/> subtype without a type check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind
{
    Type,
    Method,
    Module,
    Thread,
    Object,
}
