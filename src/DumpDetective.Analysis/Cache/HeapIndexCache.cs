using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Platform.Storage.Container;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Cache;

internal class HeapIndexCache : IDisposable
{
    private HeapIndexBuildResult? _heapIndex;
    private DumpSizeTier _sizeTier = DumpSizeTier.Medium;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private TimeSpan? _lastBuildDuration;
    private string? _lastBuildError;

    // Lazily opened on first TryGetObjectMetadata call and kept open for the cache's lifetime —
    // see docs/cache/cache-architecture.md Phase 3. _addressLookupAttempted distinguishes
    // "not tried yet" from "tried and unavailable" so a missing/aborted SegmentIndex section
    // doesn't retry TryOpen on every call.
    private ObjectAddressLookup? _addressLookup;
    private bool _addressLookupAttempted;

    // One container session for the whole run (docs/cache/cache-implementation-clean-slate-redesign.md
    // § 6.1). It holds no OS handle — only the parsed TOC and the set of sections already
    // checksum-verified — so it neither locks cache.bin nor needs disposing. Owned here, one
    // HeapIndexCache per dump, never static: two dumps are analysed in one process for
    // baseline/trend comparison.
    private CacheContainerReader? _containerSession;
    private bool _containerSessionAttempted;
    private readonly object _containerSessionGate = new();

    internal CacheContainerReader? GetOrOpenContainerSession()
    {
        if (_containerSessionAttempted)
            return _containerSession;

        lock (_containerSessionGate)
        {
            if (_containerSessionAttempted)
                return _containerSession;

            string? path = _heapIndex?.IndexPath;
            if (!string.IsNullOrWhiteSpace(path)
                && CacheContainerReader.TryOpen(path, out CacheContainerReader? reader))
            {
                _containerSession = reader;
            }

            _containerSessionAttempted = true;
            return _containerSession;
        }
    }

    public bool TryGetHeapIndex([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapIndexBuildResult? heapIndex)
    {
        heapIndex = _heapIndex;
        return heapIndex is not null;
    }

    public HeapIndexBuildResult PrebuildHeapIndex(
        ClrHeap heap,
        string dumpPath,
        CancellationToken cancellationToken,
        IProgress<AnalyzerProgressReport>? progress = null,
        IReadOnlyList<IAnalyzer>? activeAnalyzers = null,
        bool enableExactDominatorTree = false)
    {
        if (_heapIndex is not null)
            return _heapIndex;

        _progress = progress;

        try
        {
            long dumpBytes = new FileInfo(dumpPath).Length;
            _sizeTier = dumpBytes > 4L * 1024 * 1024 * 1024 ? DumpSizeTier.Large :
                        dumpBytes > 512L * 1024 * 1024 ? DumpSizeTier.Medium :
                        DumpSizeTier.Small;
        }
        catch
        {
            _sizeTier = DumpSizeTier.Medium;
        }

        IObjectIndexWriter writer = new DiskBackedObjectIndexWriter();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _heapIndex = writer.Build(
                heap, cancellationToken, progress, dumpPath, _sizeTier, activeAnalyzers, enableExactDominatorTree);
            sw.Stop();
            _lastBuildTime = DateTime.UtcNow;
            _lastBuildDuration = sw.Elapsed;
            _lastBuildError = null;
            return _heapIndex;
        }
        catch (Exception ex)
        {
            _lastBuildTime = DateTime.UtcNow;
            _lastBuildDuration = null;
            _lastBuildError = ex.ToString();
            throw;
        }

    }

    public DumpSizeTier SizeTier => _sizeTier;

    public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
    {
        _progress = progress;
    }

    public IEnumerable<HeapEntry> EnumerateIndexedEntries()
    {
        if (_heapIndex is null)
            yield break;

        CacheContainerReader? session = GetOrOpenContainerSession();
        if (session is null)
            yield break;

        foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntries(session))
            yield return entry;
    }

    public IEnumerable<HeapEntry> EnumerateIndexedEntriesRange(long startRecord, long recordCount)
    {
        if (_heapIndex is null)
            yield break;

        CacheContainerReader? session = GetOrOpenContainerSession();
        if (session is null)
            yield break;

        foreach (HeapEntry entry in ObjectIndexReader.ReadDiskEntriesRange(session, startRecord, recordCount))
            yield return entry;
    }

    public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
    {
        foreach (HeapEntry entry in EnumerateIndexedEntries())
            yield return (entry.Address, entry.MethodTable, entry.Size);
    }

    public bool TryGetObjectMetadata(ClrHeap heap, ulong address, out ulong methodTable, out ulong size)
    {
        methodTable = 0;
        size = 0;

        if (_heapIndex is not null && _heapIndex.StorageKind == HeapIndexStorageKind.Disk)
        {
            if (!_addressLookupAttempted)
            {
                _addressLookupAttempted = true;

                // Through the run's session, not a private reader: this lookup maps three object
                // columns, and a private reader would re-verify all 334.6 MiB of them that the
                // session has already checked (measurements § 7.1).
                CacheContainerReader? session = GetOrOpenContainerSession();
                if (session is not null)
                    ObjectAddressLookup.TryOpen(session, out _addressLookup);
            }

            // A disk index with a SegmentIndex section is authoritative — a miss here means
            // "not a live object", not "unavailable", so this is the final answer; no fallback.
            if (_addressLookup is not null)
                return _addressLookup.TryGetEntry(address, out methodTable, out size);
        }

        // In-memory mode, or SegmentIndex unavailable on this disk index (old cache, aborted
        // satellite write) — fall back to a live ClrMD resolution
        // so callers never need to branch on backing mode.
        if (address == 0)
            return false;

        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid)
            return false;

        methodTable = obj.Type?.MethodTable ?? 0;
        size = obj.Size;
        return true;
    }

    public void Dispose() => _addressLookup?.Dispose();

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(HeapIndexCache),
            LastBuildDurationMs = _lastBuildDuration.HasValue ? (long?)_lastBuildDuration.Value.TotalMilliseconds : null,
            LastBuildStatus = _lastBuildError is null ? "success" : "failure",
            EntryCount = _heapIndex?.ObjectCount ?? 0,
            MemoryUsageBytes = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError
        };
    }
}

