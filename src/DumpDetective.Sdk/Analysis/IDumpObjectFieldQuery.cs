namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Tier 2 (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md § "Two-tier capability
/// surface design"): reads a single named instance field's *value* off a live heap object — the one
/// capability Tier 1's coarse address/type/size streams deliberately do not cover. Backs the
/// <c>dump.object-fields</c> capability (<see cref="Artifacts.CapabilityVocabulary.DumpObjectFields"/>).
/// </summary>
/// <remarks>
/// <b>Correction to the plan's own "lives in Sources.ClrDump, not DumpDetective.Sdk" placement</b> —
/// found while building the first real consumer (<c>SqlConnectionPoolAnalyzer</c>). That placement
/// call was made before any Tier-2 consumer existed to check it against; on inspection, this
/// interface's *shape* (an address plus a field name resolves to a value) is exactly as
/// source-neutral as every Tier-1 surface already shipped in this file — no trace source will ever
/// implement it, but that is a fact about which sources can *satisfy* the capability, not about
/// where a pure, zero-ClrMD-dependency contract belongs. Keeping it here avoids inventing a second
/// placement rule for one interface; the still-nonexistent <c>Sources.ClrDump</c> project gets this
/// interface's dump-side implementation instead (today, <c>DumpDetective.Analysis/Sdk/DumpObjectFieldQuery.cs</c>,
/// matching every other Tier-1 dump-side implementation's interim home).
/// </remarks>
public interface IDumpObjectFieldQuery
{
    /// <summary>
    /// Reads the named instance field on the object at <paramref name="address"/>. Returns
    /// <c>false</c> when the object at that address is invalid, the field doesn't exist on its
    /// type, or the field's runtime shape isn't one <see cref="FieldValue"/> represents yet —
    /// callers must treat all three identically to "value unavailable," the same way a missing
    /// ClrMD field lookup is handled today. A field that legitimately holds a null reference or
    /// null string is not a failure: it returns <c>true</c> with <see cref="FieldValue.Null"/>.
    /// </summary>
    bool TryReadField(ulong address, string fieldName, out FieldValue value);
}
