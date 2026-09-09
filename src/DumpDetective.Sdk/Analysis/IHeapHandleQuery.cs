namespace DumpDetective.Sdk.Analysis;

/// <summary>Mirrors ClrMD's <c>ClrHandleKind</c> without depending on it — SDK has zero ClrMD dependency.</summary>
public enum HeapHandleKind
{
    Strong,
    WeakShort,
    WeakLong,
    Pinned,
    AsyncPinned,
    RefCounted,
    Dependent,
    Other,
}

/// <summary>
/// One GC handle. <see cref="DependentTargetAddress"/> is populated only for
/// <see cref="HeapHandleKind.Dependent"/> handles (mirrors <c>HandleSnapshot.bin</c>'s
/// <c>DependentTarget</c> column — see docs/binary-format.md).
/// </summary>
public readonly record struct HeapHandleRef(HeapHandleKind Kind, ulong Address, ulong? DependentTargetAddress = null);

/// <summary>The <c>heap.handles</c> capability.</summary>
public interface IHeapHandleQuery
{
    IEnumerable<HeapHandleRef> EnumerateHandles();
}
