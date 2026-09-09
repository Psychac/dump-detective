using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Analysis.Cache;

/// <summary>
/// Opens one disk-backed index from the run's <c>cache.bin</c> exactly once, and hands the resulting
/// provider to every caller thereafter — see
/// docs/cache/cache-implementation-clean-slate-redesign.md § 4/§ 6.2.
/// </summary>
/// <remarks>
/// <para>
/// Replaces five structurally identical classes (<c>ForwardIndexCache</c>, <c>ReverseIndexCache</c>,
/// <c>DominatorReachableIndexCache</c>, <c>DominatorTreeIndexCache</c>,
/// <c>ThreadRetentionIndexCache</c>) that differed only in type names and, in one case, an extra
/// constructor dependency — ~425 lines expressing one idea five times.
/// </para>
/// <para>
/// A missing or unopenable index is never an error: <see cref="TryGetProvider"/> returns
/// <c>null</c> and callers fall back to their own strategy, exactly as before. The single attempt is
/// remembered so a failed open isn't retried on every query.
/// </para>
/// <para>
/// <paramref name="TProvider"/> is what callers consume; the factory separately reports what needs
/// disposing, because the shape genuinely varies — the edge indices expose a provider that wraps a
/// disposable reader, the dominator readers *are* their own provider, and the thread-retention
/// provider owns nothing.
/// </para>
/// </remarks>
internal sealed class LazyContainerIndex<TProvider> : IDisposable
    where TProvider : class
{
    /// <summary>
    /// Builds the provider from an open container, or returns <c>(null, null)</c> if this index
    /// isn't present. <c>Owns</c> is the resource to dispose with the cache, when there is one.
    /// </summary>
    public delegate (TProvider? Provider, IDisposable? Owns) OpenFromContainer(CacheContainerReader container);

    private readonly string _name;
    private readonly Func<CacheContainerReader?> _getContainer;
    private readonly OpenFromContainer _open;

    private bool _attempted;
    private TProvider? _provider;
    private IDisposable? _owned;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    public LazyContainerIndex(string name, Func<CacheContainerReader?> getContainer, OpenFromContainer open)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _getContainer = getContainer ?? throw new ArgumentNullException(nameof(getContainer));
        _open = open ?? throw new ArgumentNullException(nameof(open));
    }

    /// <summary>
    /// The shared provider, or <c>null</c> if this index isn't available for the run (in-memory
    /// mode, a section that failed to write, a container predating the section, or an index this
    /// build wasn't configured to produce). Never throws.
    /// </summary>
    public TProvider? TryGetProvider()
    {
        if (_attempted)
            return _provider;

        _attempted = true;

        CacheContainerReader? container = _getContainer();
        if (container is null)
            return null;

        try
        {
            (_provider, _owned) = _open(container);
            _lastBuildTime = DateTime.UtcNow;
            _lastBuildError = null;
        }
        catch (Exception ex)
        {
            // Non-fatal: same treatment as a missing section. Callers fall back.
            _lastBuildError = $"{ex.GetType().Name}: {ex.Message}";
            _provider = null;
            _owned = null;
        }

        return _provider;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = _name,
            LastBuildStatus = _lastBuildError is null ? (_provider is null ? "unavailable" : "success") : "failure",
            EntryCount = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError,
        };
    }

    public void Dispose() => _owned?.Dispose();
}
