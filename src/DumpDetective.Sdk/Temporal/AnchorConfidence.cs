using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Temporal;

/// <summary>
/// How trustworthy a <see cref="TimeAnchor"/> is. Must propagate into findings that make temporal
/// claims, not be swallowed during alignment — fake temporal precision is the most dangerous
/// failure mode in a multi-source tool. See docs/refactor/modularity/source-model.md § 5.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnchorConfidence>))]
public enum AnchorConfidence
{
    Exact,
    Approximate,
    Unknown,
}
