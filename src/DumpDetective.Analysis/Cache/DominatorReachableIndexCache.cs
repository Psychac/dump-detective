using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily opens the disk-backed <c>DominatorReachableAddresses</c> section for the current run's
/// <c>cache.bin</c>, reusing a single <see cref="DominatorReachableAddressReader"/> (and the
/// memory-mapped view it holds) across every caller instead of re-opening per query. Mirrors
/// <see cref="ForwardIndexCache"/>'s lazy-build-from-heap-index pattern.
/// </summary>
internal sealed class DominatorReachableIndexCache : IDisposable
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    private bool _attempted;
    private DominatorReachableAddressReader? _reader;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public DominatorReachableIndexCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    /// <summary>
    /// Returns the shared reachable-address provider, or <c>null</c> if it's unavailable for this
    /// run (in-memory mode, <c>DD_SKIP_REVERSE_INDEX_BUILD=1</c> — Stage A's walk never runs
    /// without the reverse-edge index it feeds — or the section failed to write). Never throws —
    /// a missing index is treated exactly like a missing satellite section elsewhere in this
    /// codebase: callers fall back to their own alternate strategy, or skip the check.
    /// </summary>
    public IReachableAddressProvider? TryGetProvider()
    {
        if (_attempted)
            return _reader;

        _attempted = true;

        HeapIndexBuildResult? heapIndex = _getHeapIndex();
        if (heapIndex is null || string.IsNullOrEmpty(heapIndex.IndexPath))
            return null;

        try
        {
            if (!CacheContainerReader.TryOpen(heapIndex.IndexPath, out CacheContainerReader? container) || container is null)
                return null;

            if (!DominatorReachableAddressReader.TryOpen(container, out DominatorReachableAddressReader? reader) || reader is null)
                return null;

            _reader = reader;
            _lastBuildTime = DateTime.UtcNow;
            _lastBuildError = null;
        }
        catch (Exception ex)
        {
            // Non-fatal: same treatment as a missing section. Callers fall back.
            _lastBuildError = $"{ex.GetType().Name}: {ex.Message}";
            _reader = null;
        }

        return _reader;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(DominatorReachableIndexCache),
            LastBuildStatus = _lastBuildError is null ? (_reader is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }

    public void Dispose() => _reader?.Dispose();
}
