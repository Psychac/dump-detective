namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Pure range-correlation logic behind <see cref="Cache.RootSetCache.TryResolveStackFrameOwner"/>
/// (Mechanism B, see docs/analysis/root-field-name-index-plan.md), decoupled from ClrMD types so
/// it's unit-testable without a live heap/thread.
/// </summary>
internal static class StackFrameRangeCorrelator
{
    /// <summary>
    /// <paramref name="stackPointers"/> must be sorted ascending (innermost/current frame first,
    /// increasing outward toward the caller — the conventional x64 stack-growth direction).
    /// Returns the index of the frame owning <paramref name="slotAddr"/> — the last frame whose
    /// stack pointer is &lt;= <paramref name="slotAddr"/>, since a frame's local-variable range
    /// extends from its own stack pointer up to (but not including) the next frame's — or
    /// <c>-1</c> when <paramref name="slotAddr"/> falls below the innermost frame (or the list is
    /// empty).
    /// </summary>
    public static int FindOwningFrameIndex(IReadOnlyList<ulong> stackPointers, ulong slotAddr)
    {
        int lo = 0, hi = stackPointers.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (stackPointers[mid] <= slotAddr)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    /// <summary>
    /// The binary search in <see cref="FindOwningFrameIndex"/> assumes ascending order; call this
    /// first to detect a misordered/corrupted stack and skip correlation for that thread entirely
    /// rather than risk silently attributing a root to the wrong frame.
    /// </summary>
    public static bool IsSortedAscending(IReadOnlyList<ulong> stackPointers)
    {
        for (int i = 1; i < stackPointers.Count; i++)
        {
            if (stackPointers[i] < stackPointers[i - 1])
                return false;
        }

        return true;
    }
}
