using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal.Dominator;

/// <summary>
/// Sums <see cref="IDominatorTreeProvider.TryGetRetainedBytes"/> across a set of targets without
/// double-counting when one target's dominator subtree contains another — §12.1
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md). Dominance is a tree, so for
/// any two targets either one is a strict ancestor of the other or neither dominates the other —
/// never a partial overlap — which makes the exclusion below exact rather than an approximation.
/// </summary>
internal static class DominatorRetainedSetAggregator
{
    /// <summary>
    /// Returns the exact retained-byte total for the union of <paramref name="targets"/>' dominator
    /// subtrees. A target whose dominator-chain reaches another target in the same set before
    /// reaching the virtual root is skipped — its subtree is already counted inside that other
    /// target's total. Targets not reachable when the tree was built are silently skipped, same
    /// "not an error" contract as <see cref="IDominatorTreeProvider.TryGetRetainedBytes"/>.
    /// </summary>
    public static ulong ComputeExclusiveRetainedBytes(IDominatorTreeProvider provider, IReadOnlyList<ulong> targets)
    {
        if (targets.Count == 0)
            return 0;

        var targetSet = new HashSet<ulong>(targets);

        ulong total = 0;
        foreach (ulong target in targetSet)
        {
            if (IsDominatedByAnotherTarget(provider, target, targetSet))
                continue;

            if (provider.TryGetRetainedBytes(target, out ulong retainedBytes))
                total += retainedBytes;
        }

        return total;
    }

    private static bool IsDominatedByAnotherTarget(IDominatorTreeProvider provider, ulong target, HashSet<ulong> targetSet)
    {
        ulong current = target;
        while (provider.TryGetImmediateDominator(current, out ulong dominatorAddress))
        {
            if (dominatorAddress == 0)
                return false;

            if (targetSet.Contains(dominatorAddress))
                return true;

            current = dominatorAddress;
        }

        return false;
    }
}
