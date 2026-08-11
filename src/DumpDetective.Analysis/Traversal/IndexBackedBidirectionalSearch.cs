using Microsoft.Diagnostics.Runtime;

using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Traversal;

/// <summary>
/// ClrMD-aware adapter around <see cref="BidirectionalGraphSearch"/>: supplies forward neighbors
/// via <see cref="IReferenceProvider"/> (actual outgoing references) and backward neighbors via
/// <see cref="IBackwardReferenceProvider"/> (actual parents, from the disk-backed reverse-reference
/// index) — real traversal in both directions, applying the same noise-pruning/fanout-capping
/// rules as the legacy <see cref="CandidateSetBuilder"/>/<see cref="BidirectionalPathFinder"/>
/// pipeline this replaces when a reverse index is available.
///
/// Unlike that legacy pipeline — which approximates "reverse" by walking forward from the target
/// and hoping it bumps into the real retaining chain — this performs genuine reverse traversal, so
/// it cannot miss a path that exists within the search bounds, and meeting-in-the-middle covers a
/// smaller search space than one-sided BFS to the same total depth.
/// </summary>
internal sealed class IndexBackedBidirectionalSearch
{
    private readonly ClrHeap _heap;
    private readonly IReferenceProvider _forwardProvider;
    private readonly IBackwardReferenceProvider _backwardProvider;
    private readonly RootPathSearchLimits _limits;
    private readonly IPathSearchTelemetry _telemetry;
    private readonly Func<ClrType?, bool> _isNoise;
    private readonly Func<ClrType?, bool> _forceExpand;

    public IndexBackedBidirectionalSearch(
        ClrHeap heap,
        IReferenceProvider forwardProvider,
        IBackwardReferenceProvider backwardProvider,
        RootPathSearchLimits limits,
        IPathSearchTelemetry telemetry,
        Func<ClrType?, bool> isNoise,
        Func<ClrType?, bool> forceExpand)
    {
        _heap = heap;
        _forwardProvider = forwardProvider;
        _backwardProvider = backwardProvider;
        _limits = limits;
        _telemetry = telemetry;
        _isNoise = isNoise;
        _forceExpand = forceExpand;
    }

    public bool TryFindPath(
        ulong target,
        IReadOnlyList<(string RootKind, ulong Address)> roots,
        out string? rootKind,
        out List<ulong>? path,
        out bool searchTruncated,
        out int candidateSetSize,
        out int reverseIndexEntryCount,
        CancellationToken cancellationToken)
    {
        bool truncatedByIndex = false;
        int reverseCalls = 0;

        // Local iterator functions (not instance methods) so they can capture and mutate the
        // locals above via closure — iterator methods can't take ref/out parameters directly.
        IEnumerable<ulong> ForwardNeighbors(ulong node)
        {
            ClrObject obj = _heap.GetObject(node);
            if (!obj.IsValid)
                yield break;

            if (_isNoise(obj.Type))
            {
                _telemetry.IncrementPruned();
                yield break;
            }

            bool forceExpand = _forceExpand(obj.Type);
            int counted = 0;

            foreach (ulong childAddr in _forwardProvider.GetReferences(node))
            {
                counted++;
                if (!forceExpand && counted > _limits.LargeFanoutThreshold)
                {
                    _telemetry.IncrementLargeFanout();
                    yield break;
                }

                if (childAddr != 0)
                    yield return childAddr;
            }
        }

        IEnumerable<ulong> BackwardNeighbors(ulong node)
        {
            ClrObject obj = _heap.GetObject(node);
            if (!obj.IsValid)
                yield break;

            if (_isNoise(obj.Type))
            {
                _telemetry.IncrementPruned();
                yield break;
            }

            reverseCalls++;
            if (!_backwardProvider.TryGetParents(node, out IReadOnlyList<ulong> parents, out bool truncated))
                yield break;

            if (truncated)
            {
                _telemetry.IncrementLargeFanout();
                truncatedByIndex = true;
            }

            bool forceExpand = _forceExpand(obj.Type);
            int counted = 0;

            foreach (ulong parentAddr in parents)
            {
                counted++;
                if (!forceExpand && counted > _limits.LargeFanoutThreshold)
                {
                    _telemetry.IncrementLargeFanout();
                    yield break;
                }

                if (parentAddr != 0)
                    yield return parentAddr;
            }
        }

        bool found = BidirectionalGraphSearch.TryFindPath(
            target,
            roots,
            ForwardNeighbors,
            BackwardNeighbors,
            _limits.MaxCandidateNodes,
            _limits.MaxRootExpansionDepth,
            out rootKind,
            out path,
            out candidateSetSize,
            out bool budgetExhausted,
            cancellationToken);

        reverseIndexEntryCount = reverseCalls;
        searchTruncated = truncatedByIndex || (!found && budgetExhausted);
        return found;
    }
}
