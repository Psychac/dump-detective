using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One entry in the finalizer queue. <see cref="IsDisposableType"/>/<see cref="IsCriticalFinalizerType"/>
/// are structural facts (interface/base-type-chain checks) resolved once per distinct method table
/// and cached dump-side — Tier-1-shaped, unlike a specific field's *value* (e.g. a "_disposed" bool),
/// which stays behind <c>dump.object-fields</c> and is read by the consuming analyzer directly via
/// <see cref="IDumpObjectFieldQuery.TryReadField"/>, keeping this capability's tier boundary honest.
/// </summary>
/// <param name="IsValid">
/// <c>false</c> for an object ClrMD couldn't resolve (a real, if rare, possibility the pre-retyping
/// analyzer counted toward its total queue count under an "&lt;unknown&gt;" type bucket rather than
/// skipping) — unlike other Tier-1 streams that exclude invalid objects at the boundary, this one
/// doesn't, since the queue's raw total count (valid or not) is itself a fact the analyzer reports.
/// </param>
public readonly record struct FinalizerQueueObjectRef(
    ulong Address,
    bool IsValid,
    TypeRef Type,
    string TypeDisplayName,
    ulong ShallowSize,
    bool IsDisposableType,
    bool IsCriticalFinalizerType);

/// <summary>The <c>heap.finalizer-queue</c> capability — mirrors <c>heap.EnumerateFinalizableObjects()</c>.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface. Redesigned 2026-09-12 for <c>FinalizableObjectAnalyzer</c>'s retyping, its first real
/// consumer — the shipped shape (raw <see cref="ulong"/> addresses) had no type identity at all,
/// the same "declared but never checked against a real consumer" gap Batch 5/7's
/// <c>IRuntimeJitQuery</c>/<c>IHeapHandleQuery</c> redesigns hit.</remarks>
public interface IHeapFinalizerQueueQuery
{
    IEnumerable<FinalizerQueueObjectRef> EnumerateFinalizerQueue();
}
