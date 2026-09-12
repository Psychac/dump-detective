using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.reverse-references</c> capability — declared in the SDK
/// since it was added, but unimplemented until <c>CollectionAnalyzer</c>'s retyping needed it (see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md), the same "declared but never
/// checked against a real consumer" gap <c>IRuntimeJitQuery</c>/<c>IHeapHandleQuery</c>/
/// <c>IHeapFinalizerQueueQuery</c> each hit before their own first real consumer arrived. A thin
/// wrapper over <c>IHeapAnalysisCache.TryGetReverseIndexProvider</c>'s <c>IBackwardReferenceProvider</c>
/// — no in-memory fallback exists dump-side either (see that interface's own remarks), so both
/// members here simply report "unavailable" when no reverse index was built for this session.
/// </summary>
internal sealed class HeapReverseReferenceQuery : IHeapReverseReferenceQuery
{
    private readonly IHeapAnalysisCache _cache;
    private IBackwardReferenceProvider? _provider;
    private bool _resolved;

    public HeapReverseReferenceQuery(IHeapAnalysisCache cache) => _cache = cache;

    private IBackwardReferenceProvider? Provider
    {
        get
        {
            if (!_resolved)
            {
                _provider = _cache.TryGetReverseIndexProvider();
                _resolved = true;
            }

            return _provider;
        }
    }

    public IEnumerable<ulong> EnumerateReverseReferences(ulong address)
    {
        if (Provider is null || !Provider.TryGetParents(address, out IReadOnlyList<ulong> parents, out _))
            yield break;

        foreach (ulong parent in parents)
            yield return parent;
    }

    public bool TryGetReverseReferences(ulong address, out IReadOnlyList<ulong> parents, out bool truncated)
    {
        parents = [];
        truncated = false;
        return Provider is not null && Provider.TryGetParents(address, out parents, out truncated);
    }
}
