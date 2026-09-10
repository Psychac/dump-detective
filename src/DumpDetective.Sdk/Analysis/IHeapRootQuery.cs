namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Discriminates a <see cref="HeapRootRef"/>'s origin. Named after ClrMD's own <c>ClrRootKind</c>
/// but deliberately not reusing it (SDK has zero ClrMD dependency) — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
public enum HeapRootKind
{
    Stack,
    Static,
    Pinned,
    AsyncPinned,
    Handle,
    Other,
}

/// <summary>
/// One GC root. <see cref="OwnerTypeName"/>/<see cref="FieldName"/> are populated only for
/// <see cref="HeapRootKind.Static"/> roots resolved to a declaring field (mirrors
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
