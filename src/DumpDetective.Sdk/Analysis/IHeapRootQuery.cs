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
public readonly record struct HeapRootRef(
    HeapRootKind Kind,
    ulong TargetAddress,
    ulong RootAddress,
    string? OwnerTypeName = null,
    string? FieldName = null);

/// <summary>The <c>heap.roots</c> capability.</summary>
public interface IHeapRootQuery
{
    IEnumerable<HeapRootRef> EnumerateRoots();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryResolveStackFrameOwner</c> — resolves a
    /// <see cref="HeapRootKind.Stack"/> root's owning method by correlating its stack-slot address
    /// against thread frame ranges. Not an exact local-variable name; see that method's own remarks
    /// (docs/analysis/root-field-name-index-plan.md Mechanism B) for the precise scope.</summary>
    bool TryResolveStackFrameOwner(ulong rootAddress, out string ownerTypeName, out string methodName);
}
