using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.ForwardIndex;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Lazily opens the disk-backed forward-reference index for the current run's <c>cache.bin</c>,
/// reusing a single <see cref="ForwardEdgeIndexReader"/> (and the memory-mapped views it holds)
/// across every caller instead of re-opening per query. Mirrors <see cref="ReverseIndexCache"/>'s
/// lazy-build-from-heap-index pattern.
/// </summary>
internal sealed class ForwardIndexCache : IDisposable
{
    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    private bool _attempted;
    private ForwardEdgeIndexReader? _reader;
    private ForwardIndexForwardReferenceProvider? _provider;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public ForwardIndexCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    /// <summary>
    /// Returns the shared forward-reference provider, or <c>null</c> if no forward index is
    /// available for this run (in-memory mode, <c>DD_SKIP_FORWARD_INDEX_BUILD=1</c>, or the
    /// forward-index sections failed to write). Never throws — a missing index is treated exactly
    /// like a missing satellite section elsewhere in this codebase: callers fall back to their own
    /// alternate strategy (a live <c>ClrObject.EnumerateReferences</c> walk).
    /// </summary>
    public IForwardReferenceProvider? TryGetProvider()
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

            if (!ForwardEdgeIndexReader.TryOpen(container, out ForwardEdgeIndexReader? reader) || reader is null)
                return null;

            _reader = reader;
            _provider = new ForwardIndexForwardReferenceProvider(reader);
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
            Name = nameof(ForwardIndexCache),
            LastBuildStatus = _lastBuildError is null ? (_provider is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }

    public void Dispose() => _reader?.Dispose();
}
