using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Analysis;

/// <summary>How much a missing optional capability degrades confidence, relative to running with
/// it — matches the phrasing of <see cref="FidelityLevel"/> without reusing it
/// (that enum is a binary Full/Degraded fact about a single <c>Observation</c>; this is a
/// per-capability weight an analyzer declares up front, feeding into that fact once the analyzer
/// actually runs).</summary>
public enum FidelityBoost
{
    Minor,
    Moderate,
    Major,
}

/// <summary>
/// Declares a capability an analyzer can use if available but doesn't require — see
/// docs/refactor/modularity/phase-3-plugin-packaging.md's discovery-by-attribute design. Multiple
/// optional capabilities are declared with multiple attribute instances (unlike
/// <see cref="RequiresCapabilityAttribute"/>'s single-attribute/multiple-string shape), since each
/// one carries its own independent <see cref="Fidelity"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class OptionalCapabilityAttribute(string capability) : Attribute
{
    public string Capability { get; } = capability;

    public FidelityBoost Fidelity { get; init; } = FidelityBoost.Moderate;
}
