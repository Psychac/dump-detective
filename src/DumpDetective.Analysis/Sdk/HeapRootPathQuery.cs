using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Traversal;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.root-paths</c> capability, built for
/// <c>DbConnectionAnalyzer</c>/<c>TimerLeakAnalyzer</c>'s retyping
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). A thin wrapper over the
/// pre-existing <see cref="RootPathFinder"/>/<see cref="ReferenceGraph"/> machinery, not a
/// reimplementation -- bakes in the shared noise/force-expand policy
/// (<see cref="RootPathSearchSupport.IsNoisyType"/>, never force-expand) both real pre-retyping
/// callers already agreed on, leaving only <see cref="RootPathSearchOptions"/>'s numeric bounds as
/// a per-call parameter.
/// </summary>
/// <remarks>
/// <see cref="RootPathFinder"/>, valid roots, and the reverse-index provider are all resolved
/// lazily on the first <see cref="TryFindRootPath"/> call, not at construction -- both real
/// consumers only pay this cost when they actually reach their own root-path-enrichment step
/// today (a bounded, capped subset of their results), and eagerly resolving it in
/// <c>LegacyAnalysisContextTranslator.Translate</c> for every retyped analyzer, most of which never
/// call this capability at all, would be a real, avoidable cost regression.
///
/// The constructed <see cref="RootPathFinder"/>/<see cref="ReferenceGraph"/> pair is cached and
/// reused across every call on this instance, matching the pre-retyping analyzers' own pattern of
/// constructing one finder outside their enrichment loop and reusing it for every target (so its
/// internal edge cache stays warm across targets, not reset per lookup). This means the
/// <see cref="RootPathSearchOptions"/> passed to the *first* call on a given
/// <see cref="HeapRootPathQuery"/> instance governs every subsequent call too -- a real constraint,
/// not an oversight, but safe today because each retyped analyzer gets its own fresh instance
/// (translated once per <c>LegacyAnalyzerAdapter</c> invocation) and every real consumer already
/// uses one fixed options value for all of its own targets.
/// </remarks>
internal sealed class HeapRootPathQuery : IHeapRootPathQuery
{
    private readonly ClrHeap _heap;
    private readonly IHeapAnalysisCache _cache;
    private RootPathFinder? _finder;
    private IReadOnlyList<(string RootKind, ulong Address)>? _roots;

    public HeapRootPathQuery(ClrHeap heap, IHeapAnalysisCache cache)
    {
        _heap = heap;
        _cache = cache;
    }

    private bool TryFindRaw(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out List<ulong>? path, out bool searchTruncated)
    {
        rootKind = null;
        path = null;
        searchTruncated = false;

        _roots ??= _cache.GetOrBuildValidRoots(_heap);
        if (_roots.Count == 0)
            return false;

        _finder ??= new RootPathFinder(
            _heap, new ReferenceGraph(_heap),
            new RootPathSearchLimits
            {
                MaxCandidateNodes = options.MaxCandidateNodes,
                MaxCandidateDepth = options.MaxCandidateDepth,
                MaxRootExpansionDepth = options.MaxRootExpansionDepth,
                LargeFanoutThreshold = options.LargeFanoutThreshold,
            },
            RootPathSearchSupport.NoOpTelemetry, RootPathSearchSupport.IsNoisyType, static _ => false,
            _cache.TryGetReverseIndexProvider(), _cache);

        return _finder.TryFindAnyRootPath(targetAddress, _roots, out rootKind, out path, out searchTruncated, out _, out _);
    }

    public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
    {
        bool found = TryFindRaw(targetAddress, options, out string? rootKind, out List<ulong>? path, out searchTruncated);
        formattedPath = found ? RootPathSearchSupport.FormatPath(_heap, rootKind!, path, _cache) : null;
        return found;
    }

    public bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated)
    {
        hopTypeNames = null;

        bool found = TryFindRaw(targetAddress, options, out rootKind, out List<ulong>? path, out searchTruncated);
        if (!found || rootKind is null || path is null || path.Count == 0)
            return false;

        var names = new List<string>(path.Count);
        foreach (ulong address in path)
        {
            ClrType? type = RootPathSearchSupport.ResolveType(_heap, _cache, address);
            names.Add(type?.Name ?? StringConstants.UnknownType);
        }

        hopTypeNames = names;
        return true;
    }

    public bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated)
    {
        hopAddresses = null;

        bool found = TryFindRaw(targetAddress, options, out rootKind, out List<ulong>? path, out searchTruncated);
        if (!found || rootKind is null || path is null || path.Count == 0)
            return false;

        hopAddresses = path;
        return true;
    }
}
