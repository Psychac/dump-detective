using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Analysis.Readers;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Cache;

internal class RootCache
{
    private IReadOnlyList<(string RootKind, ulong Address)>? _validRoots;
    private HashSet<ulong>? _staticRootedAddresses;
    private Dictionary<ulong, string>? _rootDescriptions;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    public RootCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
    {
        _progress = progress;
    }

    public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_validRoots is not null)
            return _validRoots;

        // Prefer disk-backed RootIndex when an index exists and is on-disk.
        var builtRootIndex = _getHeapIndex();
        if (builtRootIndex is not null && builtRootIndex.StorageKind == HeapIndexStorageKind.Disk)
        {
            try
            {
                string indexDir = Path.GetDirectoryName(builtRootIndex.IndexPath) ?? string.Empty;
                string rootIndexPath = Path.Combine(indexDir, DumpIndexPaths.RootIndexFile);
                if (File.Exists(rootIndexPath))
                {
                    var roots = RootIndexReader.ReadRootTargets(rootIndexPath, CancellationToken.None);
                    _validRoots = roots;
                    _staticRootedAddresses ??= new HashSet<ulong>(capacity: Math.Max(256, roots.Count));
                    foreach (var (kind, addr) in roots)
                    {
                        if (kind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                            _staticRootedAddresses.Add(addr);
                    }

                    _lastBuildTime = DateTime.UtcNow;
                    _lastBuildError = null;
                    return _validRoots;
                }
            }
            catch
            {
                // Fall back to in-memory enumeration on any read error.
            }
        }

        EnsureRootCaches(heap);
        _lastBuildTime ??= DateTime.UtcNow;
        return _validRoots ?? Array.Empty<(string RootKind, ulong Address)>();
    }

    public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_staticRootedAddresses is not null)
            return _staticRootedAddresses;

        var roots = GetOrBuildValidRoots(heap);
        return _staticRootedAddresses ?? new HashSet<ulong>();
    }

    public string? GetRootDescription(ulong address)
    {
        if (_rootDescriptions is null)
            return null;

        _rootDescriptions.TryGetValue(address, out string? desc);
        return desc;
    }

    private void EnsureRootCaches(ClrHeap heap)
    {
        if (_staticRootedAddresses is not null && _validRoots is not null)
            return;

        _staticRootedAddresses ??= new HashSet<ulong>(capacity: 4096);
        var roots = new List<(string RootKind, ulong Address)>(capacity: 4096);
        _rootDescriptions ??= new Dictionary<ulong, string>(capacity: 4096);

        var scanCounter = new ObjectScanCounter("enumerating roots", _progress, reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            scanCounter.Tick();
            // note: caller may track object-scan counts; this class keeps local progress only

            ulong address = root.Object.Address;
            if (address == 0)
                continue;

            string kind = root.RootKind.ToString();
            roots.Add((kind, address));

            string description = root.ToString() ?? kind;
            _rootDescriptions.TryAdd(address, description);

            if (kind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase) && root.Object.IsValid)
            {
                _staticRootedAddresses.Add(address);
            }
        }

        scanCounter.Complete();
        _validRoots = roots;
        _lastBuildTime = DateTime.UtcNow;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(RootCache),
            LastBuildDurationMs = null,
            LastBuildStatus = _lastBuildError is null ? "success" : "failure",
            EntryCount = _validRoots?.Count ?? 0,
            MemoryUsageBytes = _staticRootedAddresses?.Count * sizeof(ulong) ?? 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError
        };
    }
}
