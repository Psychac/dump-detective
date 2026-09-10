using DumpDetective.Sdk.Identity;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Builds a canonical <see cref="TypeRef"/> from a dump-resolved type name — the one place this
/// happens, shared by every dump-side Tier-1 implementation that needs to construct one
/// (<see cref="HeapTypeStatisticsQuery"/>, <see cref="HeapSegmentQuery"/>), so there's exactly one
/// call site to <see cref="EntityCanonicalizer.CanonicalizeTypeName"/> to reason about.
/// </summary>
internal static class SdkTypeRefFactory
{
    public static TypeRef FromName(string rawName, ulong? methodTable)
    {
        (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(rawName);
        return new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity, MethodTable = methodTable };
    }
}
