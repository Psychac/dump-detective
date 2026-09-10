namespace DumpDetective.Sdk.Analysis;

/// <summary>Mirrors ClrMD's <c>ClrHandleKind</c> without depending on it — SDK has zero ClrMD dependency.</summary>
/// <remarks>
/// A faithful 1:1 mirror of every real <c>ClrHandleKind</c> member (verified 2026-09-10 by
/// reflecting the actual installed package, v4.0.732401 — not designed from memory/convention; see
/// docs/refactor/modularity/phase-1-sdk-review-findings.md item 20), adding <see cref="SizedRef"/>
/// and <see cref="WeakWinRT"/>, which an earlier version of this enum was missing (both would have
/// silently collapsed into <see cref="Other"/>). <see cref="Other"/> is a forward-compat catch-all
/// only, for a future ClrMD version adding something new.
/// </remarks>
public enum HeapHandleKind
{
    WeakShort,
    WeakLong,
    Strong,
    Pinned,
    RefCounted,
    Dependent,
    AsyncPinned,
    SizedRef,
    WeakWinRT,
    Other,
}

/// <summary>
/// One GC handle. <see cref="DependentTargetAddress"/> is populated only for
/// <see cref="HeapHandleKind.Dependent"/> handles (mirrors <c>HandleSnapshot.bin</c>'s
/// <c>DependentTarget</c> column — see docs/binary-format.md).
/// </summary>
public readonly record struct HeapHandleRef(HeapHandleKind Kind, ulong Address, ulong? DependentTargetAddress = null);

/// <summary>The <c>heap.handles</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapHandleQuery
{
    IEnumerable<HeapHandleRef> EnumerateHandles();
}
