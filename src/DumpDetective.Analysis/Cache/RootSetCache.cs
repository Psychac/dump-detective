using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

internal readonly record struct RootRecord(ulong TargetAddr, ulong RootAddr, byte Kind)
{
    private const byte ThreadStaticVarKind = 9;
    private const byte StaticVarKind = 10;

    public string KindName => RootIndexReader.KindToString(Kind);

    public bool IsStatic => Kind is ThreadStaticVarKind or StaticVarKind;
}

/// <summary>
/// Canonical per-run root set: reads the Phase-1 disk root index when available,
/// falling back to a live <see cref="ClrHeap.EnumerateRoots"/> walk otherwise.
/// The single source of truth for root data.
/// </summary>
internal class RootSetCache
{
    private IReadOnlyList<RootRecord>? _roots;
    private HashSet<ulong>? _staticRootedAddresses;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private string? _lastBuildError;

    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;

    public RootSetCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
    {
        _progress = progress;
    }

    public IReadOnlyList<RootRecord> GetOrBuildRoots(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_roots is not null)
            return _roots;

        var builtIndex = _getHeapIndex();
        if (builtIndex is not null)
        {
            try
            {
                var candidates = RootIndexReader.ReadRootCandidates(builtIndex, CancellationToken.None);
                if (candidates.Count > 0)
                {
                    var fromIndex = new List<RootRecord>(candidates.Count);
                    foreach ((ulong targetAddr, ulong rootAddr, byte kind) in candidates)
                        fromIndex.Add(new RootRecord(targetAddr, rootAddr, kind));

                    _roots = fromIndex;
                    _lastBuildTime = DateTime.UtcNow;
                    _lastBuildError = null;
                    return _roots;
                }
            }
            catch
            {
                // Fall back to a live heap walk on any read error.
            }
        }

        _roots = BuildFromLiveHeap(heap);
        _lastBuildTime ??= DateTime.UtcNow;
        return _roots;
    }

    public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_staticRootedAddresses is not null)
            return _staticRootedAddresses;

        var roots = GetOrBuildRoots(heap);
        var statics = new HashSet<ulong>(capacity: Math.Max(256, roots.Count));
        foreach (RootRecord root in roots)
        {
            if (root.IsStatic)
                statics.Add(root.TargetAddr);
        }

        _staticRootedAddresses = statics;
        return _staticRootedAddresses;
    }

    /// <summary>
    /// Compatibility projection for call sites still consuming the legacy
    /// <c>(string RootKind, ulong Address)</c> shape. Delete once all callers use
    /// <see cref="RootRecord"/> directly.
    /// </summary>
    public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
    {
        var roots = GetOrBuildRoots(heap);
        var projected = new List<(string RootKind, ulong Address)>(roots.Count);
        foreach (RootRecord root in roots)
            projected.Add((root.KindName, root.TargetAddr));

        return projected;
    }

    private List<RootRecord> BuildFromLiveHeap(ClrHeap heap)
    {
        var roots = new List<RootRecord>(capacity: 4096);
        var scanCounter = new ObjectScanCounter("enumerating roots", _progress, reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

        foreach (ClrRoot root in heap.EnumerateRoots())
        {
            scanCounter.Tick();

            ulong address = root.Object.Address;
            if (address == 0)
                continue;

            roots.Add(new RootRecord(address, root.Address, (byte)root.RootKind));
        }

        scanCounter.Complete();
        return roots;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(RootSetCache),
            LastBuildDurationMs = null,
            LastBuildStatus = _lastBuildError is null ? "success" : "failure",
            EntryCount = _roots?.Count ?? 0,
            MemoryUsageBytes = _staticRootedAddresses?.Count * sizeof(ulong) ?? 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError
        };
    }
}
