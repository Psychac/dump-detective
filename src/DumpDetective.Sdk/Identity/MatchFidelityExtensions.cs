namespace DumpDetective.Sdk.Identity;

/// <summary>
/// The one place in the codebase that should know <see cref="MatchFidelity"/>'s ordinal-order
/// contract is safe to compare against — see that enum's own remarks. Callers combining fidelities
/// should go through here rather than writing their own <c>&lt;</c> comparison.
/// </summary>
public static class MatchFidelityExtensions
{
    /// <summary>
    /// The weaker of two fidelities — what a join lineage degrades to when it passes through more
    /// than one canonicalized entity (e.g. a method's overall trustworthiness can't exceed its
    /// least-trustworthy parameter type, or a cross-source correlation can't exceed its
    /// weakest-matched entity).
    /// </summary>
    public static MatchFidelity Min(this MatchFidelity a, MatchFidelity b) => a < b ? a : b;
}
