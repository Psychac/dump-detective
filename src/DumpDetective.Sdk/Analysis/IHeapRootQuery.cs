namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Discriminates a <see cref="HeapRootRef"/>'s origin. Named after ClrMD's own <c>ClrRootKind</c>
/// but deliberately not reusing it (SDK has zero ClrMD dependency) — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// A faithful 1:1 mirror of every real <c>ClrRootKind</c> member (verified 2026-09-10 by reflecting
/// the actual installed package, v4.0.732401 — not designed from memory/convention; see
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 20) except <c>None</c>, ClrMD's own
/// sentinel/unset value that no real enumerated root ever has. <c>StaticVar</c>/<c>ThreadStaticVar</c>
/// are kept as two distinct values, not collapsed into one <c>Static</c> — that distinction is
/// already actively used in real production code (<c>StaticRootLeakDetector</c>). <c>Other</c> is a
/// forward-compat catch-all only, for a future ClrMD version adding something new — not, as an
/// earlier version of this enum did, a bucket that silently swallowed known, current values
/// (<c>FinalizerQueue</c> had no representation at all; <c>StrongHandle</c>/<c>RefCountedHandle</c>/
/// <c>SizedRefHandle</c> were collapsed into one generic <c>Handle</c>, inconsistent with
/// <c>PinnedHandle</c>/<c>AsyncPinnedHandle</c> already being kept separate).
/// </remarks>
public enum HeapRootKind
{
    Stack,
    StaticVar,
    ThreadStaticVar,
    FinalizerQueue,
    StrongHandle,
    PinnedHandle,
    AsyncPinnedHandle,
    RefCountedHandle,
    SizedRefHandle,
    Other,
}

/// <summary>
/// One GC root. <see cref="OwnerTypeName"/>/<see cref="FieldName"/>/<see cref="AppDomainId"/> are
/// populated only for <see cref="HeapRootKind.StaticVar"/>/<see cref="HeapRootKind.ThreadStaticVar"/>
/// roots resolved to a declaring field (mirrors
/// <c>IHeapAnalysisCache.GetStaticFieldsByRootAddress</c>); left <c>null</c> otherwise rather than
/// forcing every root through a resolution step most callers don't need.
/// </summary>
/// <remarks>
/// Named `required` properties, not positional construction — <see cref="TargetAddress"/> and
/// <see cref="RootAddress"/> are adjacent same-typed (`ulong`) fields that would be silently
/// transposable at a positional call site, semantically dangerous to swap (which one is kept alive
/// vs. which is the root's own storage location) and undetectable by the compiler. See
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 5.
/// </remarks>
public readonly record struct HeapRootRef
{
    public required HeapRootKind Kind { get; init; }
    public required ulong TargetAddress { get; init; }
    public required ulong RootAddress { get; init; }
    public string? OwnerTypeName { get; init; }
    public string? FieldName { get; init; }

    /// <summary>The declaring field's AppDomain id, or <c>null</c> alongside <see cref="OwnerTypeName"/>/
    /// <see cref="FieldName"/> when not resolved. Kept distinct from the default (id 1) so callers can
    /// tell "unresolved" apart from "resolved, default AppDomain" without a sentinel value.</summary>
    public int? AppDomainId { get; init; }
}

/// <summary>The <c>heap.roots</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapRootQuery
{
    IEnumerable<HeapRootRef> EnumerateRoots();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryResolveStackFrameOwner</c> — resolves a
    /// <see cref="HeapRootKind.Stack"/> root's owning method by correlating its stack-slot address
    /// against thread frame ranges. Not an exact local-variable name; see that method's own remarks
    /// (docs/analysis/root-field-name-index-plan.md Mechanism B) for the precise scope.</summary>
    bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName);
}
