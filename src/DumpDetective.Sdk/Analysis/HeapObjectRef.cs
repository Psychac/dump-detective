using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// A live heap object, source-neutral. <see cref="Type"/> carries a <see cref="TypeRef"/> for
/// cross-source identity instead of a raw dump-local <c>MethodTable</c>. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-1 surface table.
/// </summary>
/// <remarks>
/// <see cref="TypeDisplayName"/> added 2026-09-11, first real construction site
/// (<c>HeapSegmentQuery.EnumerateObjects</c>, the <c>HeapTopologyAnalyzer</c> retyping): a report
/// that groups/labels objects by type needs the dump's own resolved display name verbatim, not
/// <see cref="TypeRef.CanonicalName"/> — canonicalization is a cross-source join key, and
/// deliberately rewrites exactly the names a report must show unchanged (async state machines
/// unwrap to their declaring method, lambda/closure ordinals get stripped, ...). Carrying both
/// avoids forcing every display-facing consumer to either violate <c>TypeRef</c>'s "never computed
/// ad hoc" contract by re-deriving a name, or accept a silently-rewritten label.
/// </remarks>
public readonly record struct HeapObjectRef(ulong Address, TypeRef Type, ulong Size, string TypeDisplayName);
