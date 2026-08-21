using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily opens the disk-backed dominator-tree sections for the current run's <c>cache.bin</c>,
/// reusing a single <see cref="DominatorTreeReaderProvider"/> (and the memory-mapped views it holds)
/// across every caller instead of re-opening per query. Mirrors
/// <see cref="DominatorReachableIndexCache"/>'s lazy-build-from-heap-index pattern (§10.4, Batch 3,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md).
/// </summary>
internal sealed class DominatorTreeIndexCache : IDisposable
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    private bool _attempted;
    private DominatorTreeReaderProvider? _provider;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public DominatorTreeIndexCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    /// <summary>
    /// Returns the shared dominator-tree provider, or <c>null</c> if it's unavailable for this run
    /// (in-memory mode, Stage B not gated on for this run, a legacy pre-Stage-B cache.bin, or Stage
    /// B failed to persist). Never throws — a missing tree is treated exactly like a missing
    /// satellite section elsewhere in this codebase: callers fall back to their own alternate
    /// strategy (the existing top-K heuristic), or skip the check.
    /// </summary>
    public IDominatorTreeProvider? TryGetProvider()
    {
        if (_attempted)
            return _provider;

        _attempted = true;

        HeapIndexBuildResult? heapIndex = _getHeapIndex();
        if (heapIndex is null || string.IsNullOrEmpty(heapIndex.IndexPath))
            return null;

        try
        {
            if (!CacheContainerReader.TryOpen(heapIndex.IndexPath, out CacheContainerReader? container) || container is null)
                return null;

            if (!DominatorTreeReaderProvider.TryOpen(container, out DominatorTreeReaderProvider? provider) || provider is null)
                return null;

            _provider = provider;
            _lastBuildTime = DateTime.UtcNow;
            _lastBuildError = null;
        }
        catch (Exception ex)
        {
            // Non-fatal: same treatment as a missing section. Callers fall back.
            _lastBuildError = $"{ex.GetType().Name}: {ex.Message}";
            _provider = null;
        }

        return _provider;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(DominatorTreeIndexCache),
            LastBuildStatus = _lastBuildError is null ? (_provider is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }

    public void Dispose() => _provider?.Dispose();
}
