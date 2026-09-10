using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>One managed thread, source-neutral. <see cref="StackRootCount"/> mirrors
/// <c>IHeapAnalysisCache.GetOrCountThreadStackRoots</c> — capped, not exhaustive, by design (see
/// that method's own remarks).</summary>
public readonly record struct RuntimeThreadRef(ThreadRef Thread, bool IsGCSuspendPending, int StackRootCount);

/// <summary>The <c>runtime.threads</c> capability.</summary>
/// <remarks>Sync <see cref="IEnumerable{T}"/>, no <see cref="CancellationToken"/> — see
/// <see cref="IHeapObjectStream"/>'s remarks for why, shared by every Tier-1 streaming
/// surface.</remarks>
public interface IRuntimeThreadQuery
{
    IEnumerable<RuntimeThreadRef> EnumerateThreads();
}
