namespace DumpDetective.Sdk;

/// <summary>
/// Version of the source-neutral SDK contract surface (identity, capability, observation,
/// synthesis). Independent of the product's own version — plugins and schema files pin against
/// this, not against a product release. See docs/refactor/modularity/phase-1-contracts-sdk.md.
/// </summary>
/// <remarks>
/// <b>Bump policy, decided 2026-09-10:</b> <see cref="Minor"/> bumps once per fix that changes the
/// SDK's public API surface (added/removed/renamed member, changed signature, changed documented
/// behavior a caller could depend on) — including additive, non-breaking changes, not just breaking
/// ones; <see cref="Major"/> is reserved for an actual breaking change once something real depends
/// on this assembly. Bump in the same commit as the fix, not batched later — this field went
/// untouched across two whole sessions of API changes (docs/refactor/modularity/phase-1-sdk-review-findings.md
/// item 10) specifically because "bump it eventually" never has a forcing function. This is that
/// forcing function.
/// </remarks>
public static class SdkVersion
{
    public const int Major = 0;

    /// <summary>7 as of 2026-09-11 (added <see cref="Analysis.IDumpObjectFieldQuery.TryGetStringValue"/>
    /// — a second Tier-2 <c>dump.object-fields</c> primitive for "interpret this address as a string
    /// directly," found necessary by <c>HttpObjectAnalyzer</c>'s retyping: several field-chase chains
    /// call <c>ClrObject.AsString()</c> on a field's object-reference *result*, not on a named
    /// string-typed field, which <c>TryReadField</c> alone can't express).
    /// Before that, 6 as of 2026-09-11 (added <see cref="Analysis.IDumpArrayShapeQuery"/>,
    /// <c>CapabilityVocabulary.DumpArrayElements</c>, <c>Analysis.AnalysisContext.DumpArrayShapes</c>,
    /// and <see cref="Analysis.HeapTypeStatistics"/>'s <c>ArrayElementTypeName</c>/<c>ArrayRank</c>/
    /// <c>ArrayElementIsObjectReference</c>/<c>ArrayElementIsSupportedNumeric</c>/<c>ArrayElementSizeBytes</c>
    /// fields — <c>ArrayAnalyzer</c>'s retyping, the Tier-2 wave's second new capability). Also
    /// covers the untracked bump this field owed from <c>ObjectShapeAnalyzer</c>'s retyping earlier
    /// the same session (<c>HeapTypeStatistics.TotalFields</c>/<c>ReferenceFields</c>/<c>ValueFields</c>/
    /// <c>IsValueType</c>/<c>IsArray</c>/<c>BaseTypeChainDepth</c>/<c>InterfaceCount</c> and
    /// <c>IHeapTypeStatisticsQuery.HasTypeShapeData</c>) — missed at the time, folded in here rather
    /// than issuing a separate bump for a change already shipped.
    /// Before that, 5 as of 2026-09-11 (added <see cref="Analysis.IDumpObjectFieldQuery"/>,
    /// <see cref="Analysis.FieldValue"/>, <c>CapabilityVocabulary.DumpObjectFields</c>, and
    /// <c>Analysis.AnalysisContext.DumpObjectFields</c> — the Tier-2 pilot's new capability surface).
    /// Before that, 4 as of 2026-09-10 (removed <c>CapabilityVocabulary.TemporalSeries</c>, follow-on
    /// to finding 11's <c>TemporalKind.Series</c> removal) — bumped from 3 (finding 11 itself). Before
    /// that, one catch-up bump (0.1 → 0.2) covered every SDK API change already made this session
    /// before the per-finding policy above existed (the `Analysis/` namespace, capability
    /// additions/splits, the `EntityRef`/`IHeapDominatorQuery`/positional-record/JSON-converter
    /// fixes) — not a precise per-change reconstruction, since nothing yet depends on intermediate
    /// values enough to make that worth simulating.</summary>
    public const int Minor = 7;

    public static string AsString => $"{Major}.{Minor}";
}
