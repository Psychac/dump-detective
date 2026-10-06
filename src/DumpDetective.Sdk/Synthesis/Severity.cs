using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Synthesis;

/// <summary>
/// A <see cref="Claim"/>'s severity. Deliberately a distinct SDK-owned type, not a reuse of
/// <c>DumpDetective.Sdk.Synthesis.Severity</c> — the SDK has zero dependencies beyond the BCL,
/// so it cannot reference Core, and this trimmed Phase 1 pass does not move
/// <c>Core.Enums.Severity</c> here (see docs/refactor/modularity-plan.md § 8).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Info,
    Warning,
    Critical,
}
