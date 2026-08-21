using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
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

        foreach (HeapEntry entry in ObjectIndexReader.Instance.ReadEntries(_heapIndex.IndexPath))
            yield return entry;
    }

    public IEnumerable<HeapEntry> EnumerateIndexedEntriesRange(long startRecord, long recordCount)
    {
        if (_heapIndex is null)
            yield break;

        foreach (HeapEntry entry in ObjectIndexReader.Instance.ReadEntriesRange(_heapIndex.IndexPath, startRecord, recordCount))
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
                ObjectAddressLookup.TryOpen(_heapIndex.IndexPath, out _addressLookup);
            }

            // A disk index with a SegmentIndex section is authoritative — a miss here means
            // "not a live object", not "unavailable", so this is the final answer; no fallback.
            if (_addressLookup is not null)
                return _addressLookup.TryGetEntry(address, out methodTable, out size);
        }

        // In-memory mode, or SegmentIndex unavailable on this disk index (old cache, aborted
        // satellite write, DD_SKIP_SEGMENT_INDEX_BUILD=1) — fall back to a live ClrMD resolution
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

