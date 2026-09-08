using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Cache;

internal class ThreadCache
{
    private Dictionary<(ulong ThreadAddress, int MaxStackRootsToCount), int>? _threadStackRootCountCache;
    private DateTime? _lastAccess;

    public int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount)
    {
        if (thread is null)
            throw new ArgumentNullException(nameof(thread));

        if (thread.Address == 0 || maxStackRootsToCount <= 0)
            return 0;

        _threadStackRootCountCache ??= new Dictionary<(ulong ThreadAddress, int MaxStackRootsToCount), int>(capacity: 256);
        var key = (thread.Address, maxStackRootsToCount);
        if (_threadStackRootCountCache.TryGetValue(key, out int cachedCount))
            return cachedCount;

        int count = 0;
        foreach (var _ in thread.EnumerateStackRoots())
        {
            if (count >= maxStackRootsToCount)
                break;
            count++;
        }

        _threadStackRootCountCache[key] = count;
        _lastAccess = DateTime.UtcNow;
        return count;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(ThreadCache),
            LastBuildDurationMs = null,
            LastBuildStatus = "success",
            EntryCount = _threadStackRootCountCache?.Count ?? 0,
            MemoryUsageBytes = 0,
            LastBuildTime = _lastAccess,
            IsHealthy = true
        };
    }
}
