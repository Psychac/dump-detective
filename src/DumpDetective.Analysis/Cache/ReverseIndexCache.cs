using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily opens the disk-backed reverse-reference index for the current run's <c>cache.bin</c>,
/// reusing a single <see cref="ReverseEdgeIndexReader"/> (and the memory-mapped views it holds)
/// across every caller instead of re-opening per query. Mirrors <see cref="RootSetCache"/>'s
/// lazy-build-from-heap-index pattern.
/// </summary>
internal sealed class ReverseIndexCache : IDisposable
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    private bool _attempted;
    private ReverseEdgeIndexReader? _reader;
    private ReverseIndexBackwardReferenceProvider? _provider;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public ReverseIndexCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    /// <summary>
    /// Returns the shared backward-reference provider, or <c>null</c> if no reverse index is
    /// available for this run (in-memory mode, <c>DD_SKIP_REVERSE_INDEX_BUILD=1</c>, a pre-v4
    /// cache.bin, or the reverse-index sections failed to write). Never throws — a missing index
    /// is treated exactly like a missing satellite section elsewhere in this codebase: callers
    /// fall back to their own alternate strategy.
    /// </summary>
    public IBackwardReferenceProvider? TryGetProvider()
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

            if (!ReverseEdgeIndexReader.TryOpen(container, out ReverseEdgeIndexReader? reader) || reader is null)
                return null;

            _reader = reader;
            _provider = new ReverseIndexBackwardReferenceProvider(reader);
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
            Name = nameof(ReverseIndexCache),
            LastBuildStatus = _lastBuildError is null ? (_provider is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }

    public void Dispose() => _reader?.Dispose();
}
