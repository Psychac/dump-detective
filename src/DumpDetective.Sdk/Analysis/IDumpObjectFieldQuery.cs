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

    /// <summary>
    /// Interprets the object at <paramref name="address"/> directly as a string, mirroring ClrMD's
    /// own permissive <c>ClrObject.AsString()</c> (which several pre-retyping Tier-2 analyzers call
    /// on a field's object-reference *result*, not a named string-typed field — e.g. reading an
    /// HttpClient's <c>_baseAddress</c> field, an object-reference to a <c>System.Uri</c>, then
    /// calling <c>.AsString()</c> on that <c>Uri</c> object directly). Added 2026-09-11 for
    /// <c>HttpObjectAnalyzer</c>/<c>WcfChannelAnalyzer</c>'s retyping — a second, narrower primitive
    /// alongside <see cref="TryReadField"/> rather than folding "chase to object then treat as
    /// string" into that method, since the object being chased to isn't always reached by a single
    /// named field (some call sites chase through two or three fields first via repeated
    /// <see cref="TryReadField"/> calls, only the final hop needs this).
    /// </summary>
    bool TryGetStringValue(ulong address, out string? value);

    /// <summary>
    /// Resolves the *declared* type name of a named instance field on the object at
    /// <paramref name="address"/>'s type — no value read at all, so this works even for a
    /// struct-typed field <see cref="TryReadField"/> can't represent a value for (e.g. detecting a
    /// compiler-generated <c>AsyncVoidMethodBuilder</c>-typed builder field, a structural check with
    /// no runtime value to inspect). Returns <c>false</c> when the object at that address is invalid
    /// or the field doesn't exist on its type. Added 2026-09-12 for
    /// <c>AsyncStateMachineAnalyzer</c>'s retyping.
    /// </summary>
    bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName);

    /// <summary>
    /// Every object-reference-typed instance field declared on the object at <paramref name="address"/>'s
    /// type, as <c>(Name, TargetAddress)</c> — <c>TargetAddress</c> is 0 for a null reference. Resolve
    /// each target's size/type via the existing <see cref="IHeapObjectLookup"/> rather than duplicating
    /// that here. Returns an empty list when the object at that address is invalid, not when it simply
    /// has no reference fields (both look the same to a caller). Added 2026-09-12 for
    /// <c>AsyncStateMachineAnalyzer</c>'s retyping — the field-*name* enumeration gap
    /// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md called out as still open:
    /// nothing shipped before this let a retyped analyzer ask "what are this instance's reference
    /// fields called," only "read this *named* field's value."
    /// </summary>
    IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address);
}
