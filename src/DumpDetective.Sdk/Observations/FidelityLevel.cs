using System.Text.Json.Serialization;

namespace DumpDetective.Sdk.Observations;

/// <summary>Whether the analyzer that produced an <see cref="Observation"/> got every capability it
/// could have used, or ran without one or more optional capabilities. Flows into confidence scoring
/// as the "capability-fidelity factor" — see docs/refactor/modularity/observation-and-correlation-model.md § 4.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FidelityLevel>))]
public enum FidelityLevel
{
    Full,
    Degraded,
}
