using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily opens the disk-backed <c>RootStackThreadAttribution</c>/<c>Roots</c> sections and
/// cross-references them with the dominator tree, exactly once per run — §12.2
/// (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md), mirroring
/// <see cref="DominatorTreeIndexCache"/>'s lazy-open pattern.
/// </summary>
internal sealed class ThreadRetentionIndexCache
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;
    private readonly Func<IDominatorTreeProvider?> _getTreeProvider;

    private bool _attempted;
    private ThreadRetentionReaderProvider? _provider;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public ThreadRetentionIndexCache(Func<HeapIndexBuildResult?> getHeapIndex, Func<IDominatorTreeProvider?> getTreeProvider)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
        _getTreeProvider = getTreeProvider ?? throw new ArgumentNullException(nameof(getTreeProvider));
    }

    /// <summary>
    /// Returns the shared thread-retention provider, or <c>null</c> if it's unavailable for this run
    /// (in-memory mode, the dominator tree itself unavailable, a legacy pre-§12.2 cache.bin, or the
    /// section failed to persist). Never throws — same "not an error" contract as
    /// <see cref="DominatorTreeIndexCache.TryGetProvider"/>.
    /// </summary>
    public IThreadRetentionProvider? TryGetProvider(CancellationToken cancellationToken = default)
    {
        if (_attempted)
            return _provider;

        _attempted = true;

        HeapIndexBuildResult? heapIndex = _getHeapIndex();
        if (heapIndex is null || string.IsNullOrEmpty(heapIndex.IndexPath))
            return null;

        IDominatorTreeProvider? treeProvider = _getTreeProvider();
        if (treeProvider is null)
            return null;

        try
        {
            if (!CacheContainerReader.TryOpen(heapIndex.IndexPath, out CacheContainerReader? container) || container is null)
                return null;

            if (!ThreadRetentionReaderProvider.TryOpen(container, treeProvider, cancellationToken, out ThreadRetentionReaderProvider? provider) || provider is null)
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
            Name = nameof(ThreadRetentionIndexCache),
            LastBuildStatus = _lastBuildError is null ? (_provider is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }
}
