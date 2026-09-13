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
    /// Same as <see cref="TryGetStringValue(ulong, out string?)"/>, but caps how much of the
    /// string's content is materialized — mirrors ClrMD's own <c>ClrObject.AsString(int maxLength)</c>
    /// overload, which several pre-retyping Tier-2 analyzers use specifically to avoid allocating a
    /// multi-megabyte <see cref="string"/> just to hash a fingerprint or build a short preview off
    /// it. Added 2026-09-13 for <c>StringAnalyzer</c>'s retyping — a real, closable perf gap: the
    /// unbounded overload above was fine for every existing consumer (all read short, well-known
    /// fields), but this is the first consumer that reads arbitrary, attacker/workload-sized string
    /// content directly.
    /// </summary>
    bool TryGetStringValue(ulong address, int maxLength, out string? value);

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

    /// <summary>
    /// Reads <paramref name="innerFieldName"/> off the embedded struct held in
    /// <paramref name="address"/>'s <paramref name="outerFieldName"/> field — one level of
    /// struct nesting, e.g. reading <c>ManualResetValueTaskSourceCore&lt;TResult&gt;._completed</c>
    /// off a class's embedded core field. Returns <c>false</c> when the object address is invalid,
    /// <paramref name="outerFieldName"/> doesn't exist or isn't a struct-typed field,
    /// <paramref name="innerFieldName"/> doesn't exist on that struct, or the inner field's runtime
    /// shape isn't one <see cref="FieldValue"/> represents (same "unavailable" semantics as
    /// <see cref="TryReadField"/>). Deliberately not a general N-level field-chase API — exactly the
    /// one level of nesting a real Tier-2 consumer needs today; a deeper chase can compose two calls
    /// once the first one lands on another struct-typed field, but nothing needs that yet. Added
    /// 2026-09-12 for <c>AsyncTaskAnalyzer</c>'s retyping.
    /// </summary>
    bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value);

    /// <summary>
    /// Every instance field declared on the object at <paramref name="address"/>'s type, as its
    /// name plus enough runtime shape to classify it without a value read: <see cref="FieldValueKind"/>
    /// (best-effort — a field whose runtime shape isn't one <see cref="FieldValue"/> represents
    /// reads as <see cref="FieldValueKind.Null"/> here too, same "unavailable" convention as
    /// <see cref="TryReadField"/>) and whether it's array-typed (<c>SZArray</c>/<c>Array</c> collapse
    /// into <see cref="FieldValueKind.ObjectReference"/> like every other reference field, so this
    /// flag is the only way to tell an array-typed field apart from a plain object reference).
    /// Returns an empty list when the object at that address is invalid, not when the type simply
    /// has no fields (both look the same to a caller). Added 2026-09-12 for <c>CollectionAnalyzer</c>'s
    /// retyping — a genuinely different gap from <see cref="EnumerateObjectReferenceFields"/>: that
    /// one only lists reference-typed fields by name; this one classifies *every* field (including
    /// <see cref="FieldValueKind.Int32"/> ones) well enough to run the same "well-known field names
    /// first, then classify-and-guess" heuristic <c>CollectionAnalyzer</c>'s pre-retyping
    /// <c>GetOrBuildFieldLayout</c> used, without needing a real <c>ClrType</c>/<c>ClrInstanceField</c>
    /// at all.
    /// </summary>
    IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address);

    /// <summary>
    /// The runtime-computed managed stack trace captured on an exception object at
    /// <paramref name="exceptionAddress"/> (mirrors <c>ClrObject.AsException()?.EnumerateExceptionStackTrace()</c>),
    /// capped at <paramref name="maxFrames"/>, innermost frame first. Distinct from reading the
    /// <c>"_stackTraceString"</c> field via <see cref="TryReadField"/>/<see cref="TryGetStringValue"/>:
    /// this walks the exception's internal binary <c>_stackTrace</c> trace-info array (a native
    /// array of instruction-pointer/stack-pointer/method-handle triples, resolved to
    /// <c>ClrMethod</c>s), which is populated whenever a thread has actually unwound through the
    /// throw site — including before <c>_stackTraceString</c> has ever been materialized as text
    /// (e.g. an exception sitting in a field/collection that has never had <c>.ToString()</c>/
    /// <c>.StackTrace</c> called on it). Added 2026-09-13 for <c>CrashAnalyzer</c>'s retyping, the
    /// third of its three pre-retyping fallback tiers (<c>_stackTraceString</c> text, this raw
    /// unwind, then <c>_remoteStackTraceString</c>) — the only one with no field-value equivalent,
    /// since <c>ClrObject.AsException()</c>'s <c>Message</c>/<c>HResult</c>/<c>Inner</c> properties
    /// were confirmed (by decompiling the installed ClrMD package) to already be plain
    /// <c>"_message"</c>/<c>"_HResult"</c>/<c>"_innerException"</c> field reads under the hood, with
    /// no equivalent gap to close. Returns an empty list for an invalid address, a type that isn't
    /// an exception, or when no trace is captured.
    /// </summary>
    IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames);

    /// <summary>
    /// Whether the object at <paramref name="address"/>'s type declares any delegate-typed field —
    /// instance (<c>ClrType.Fields</c>) or static (<c>ClrType.StaticFields</c>; a field-like event
    /// such as <c>public static event EventHandler Foo;</c> compiles to a static backing delegate
    /// field, not an instance one, so both must be checked). A structural fact about the *declared
    /// type*, not a per-field value read — a field's <c>ElementType</c> alone (see
    /// <see cref="InstanceFieldDescriptor.Kind"/>) can't distinguish a delegate field from any other
    /// object-reference field; this walks the field's own declared type's base-type chain for
    /// <c>System.MulticastDelegate</c>. Returns <c>false</c> for an invalid address. Added
    /// 2026-09-13 for <c>StaticRootLeakDetector</c>'s retyping.
    /// </summary>
    bool HasDelegateField(ulong address);
}

/// <summary>One instance field's name and declared shape, from <see cref="IDumpObjectFieldQuery.EnumerateInstanceFields"/>.</summary>
public readonly record struct InstanceFieldDescriptor(string Name, FieldValueKind Kind, bool IsArrayTyped);
