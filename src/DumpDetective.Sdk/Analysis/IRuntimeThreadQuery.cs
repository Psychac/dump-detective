using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>One managed thread, source-neutral. <see cref="StackRootCount"/> mirrors
/// <c>IHeapAnalysisCache.GetOrCountThreadStackRoots</c> — capped, not exhaustive, by design (see
/// that method's own remarks). <see cref="IsAlive"/> added 2026-09-11 for <c>JitAnalyzer</c>'s
/// retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md) — mirrors
/// <c>ClrThread.IsAlive</c>; <see cref="EnumerateThreads"/> yields every thread, alive or not,
/// leaving the alive-only filtering an analyzer's own pre-retyping code already did to the analyzer,
/// not baked into the stream itself.</summary>
public readonly record struct RuntimeThreadRef(ThreadRef Thread, bool IsGCSuspendPending, int StackRootCount, bool IsAlive);

/// <summary>
/// One stack frame, source-neutral. <see cref="HasMethod"/> distinguishes "not a managed-method
/// frame at all" from "a managed-method frame ClrMD couldn't resolve a <c>ClrMethod</c> for" — a
/// real, if rare, case the pre-retyping analyzer had to skip explicitly; every field past
/// <see cref="IsManagedMethod"/> is meaningless (default) when <see cref="HasMethod"/> is
/// <c>false</c>.
/// </summary>
/// <remarks>
/// <see cref="DeclaringTypeName"/>/<see cref="ModuleName"/> are raw ClrMD display names (falling
/// back to <c>"Unknown"</c>), not canonicalized <see cref="TypeRef"/>/<see cref="ModuleRef"/> —
/// <c>JitAnalyzer</c>'s only use for them is a report-facing hotspot count keyed by exact name, the
/// same reasoning <c>HeapObjectRef.TypeDisplayName</c> was added for (docs/refactor/modularity/
/// phase-1-full-extraction-retyping-plan.md, Batch 2). <see cref="ModuleName"/> is the raw
/// <c>ClrModule.Name</c> (typically a full file path), deliberately not reduced to a file name —
/// matches the pre-retyping analyzer's own key exactly, kept joinable against
/// <c>ModuleDomainResult</c>'s equally-raw module names.
/// </remarks>
public readonly record struct ThreadStackFrameRef(
    bool IsManagedMethod,
    bool HasMethod,
    string DeclaringTypeName,
    string ModuleName,
    bool IsDynamicModule,
    bool IsReadyToRun,
    ulong MethodDesc,
    ulong NativeCodeAddress,
    uint HotSize,
    uint ColdSize,
    string MethodDisplayName);

/// <summary>The <c>runtime.threads</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IRuntimeThreadQuery
{
    IEnumerable<RuntimeThreadRef> EnumerateThreads();

    /// <summary>
    /// Walks <paramref name="thread"/>'s full stack (managed and unmanaged frames), unbounded — no
    /// frame-count cap, matching <c>ThreadAnalyzer</c>'s own precedent (§11.4 M8: measured no cost
    /// concern walking a real dump's deepest stack, 135 threads, 2 ms) rather than the
    /// shallower caps some earlier code used. <paramref name="thread"/> must have come from this
    /// same query's <see cref="EnumerateThreads"/>.
    /// </summary>
    IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread);
}
