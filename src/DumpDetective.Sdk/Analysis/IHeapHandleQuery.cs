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
/// One GC handle. Redesigned 2026-09-11 for <c>GCHandleAnalyzer</c>'s retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — the original shape
/// (<c>Address</c>, no target-type field) had zero real consumers and didn't match what the one
/// analyzer that actually needs this capability does.
/// </summary>
/// <param name="TargetAddress">The handle's target object address, or 0 for a handle with no live
/// target (mirrors <c>HandleRecord.Address</c>).</param>
/// <param name="TargetTypeDisplayName">
/// The target's type display name, resolved from the handle record's own captured method table —
/// not a live lookup at <see cref="TargetAddress"/> — so it stays resolvable even for a
/// since-collected weak-handle target (a method table is a per-*type*, not per-instance, EE
/// structure that outlives any specific collected instance). Empty when <see cref="TargetAddress"/>
/// is 0. Falls back to <c>"Object@0x{address:X}"</c> (not the SDK-wide <c>"MT:0x{mt:x}"</c>
/// convention used elsewhere) when the method table itself doesn't resolve to a <c>ClrType</c> —
/// matches the pre-retyping analyzer's own fallback exactly, a deliberate, narrow divergence from
/// the shared convention for the same reason <c>HeapObjectLookup</c>'s divergence was accepted in
/// the GC-root retyping batch.
/// </param>
/// <param name="DependentTargetAddress">Populated only for <see cref="HeapHandleKind.Dependent"/>
/// handles (mirrors <c>HandleSnapshot.bin</c>'s <c>DependentTarget</c> column).</param>
public readonly record struct HeapHandleRef(
    HeapHandleKind Kind,
    ulong TargetAddress,
    string TargetTypeDisplayName,
    ulong? DependentTargetAddress = null);

/// <summary>The <c>heap.handles</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IHeapHandleQuery
{
    IEnumerable<HeapHandleRef> EnumerateHandles();
}
