using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Cache;

internal class HeapIndexCache
{
    private HeapIndexBuildResult? _heapIndex;
    private DumpSizeTier _sizeTier = DumpSizeTier.Medium;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private TimeSpan? _lastBuildDuration;
    private string? _lastBuildError;

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
        HeapIndexPrebuildMode mode = HeapIndexPrebuildMode.Auto)
    {
        if (_heapIndex is not null)
            return _heapIndex;

        _progress = progress;

        HeapIndexPrebuildMode selectedMode = SelectPrebuildMode(mode, dumpPath);
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

        IObjectIndexWriter writer = selectedMode == HeapIndexPrebuildMode.Memory
            ? new MemoryBackedObjectIndexWriter()
            : new DiskBackedObjectIndexWriter();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _heapIndex = writer.Build(heap, cancellationToken, progress, dumpPath, _sizeTier);
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

        if (_heapIndex.StorageKind == HeapIndexStorageKind.Memory)
        {
            if (_heapIndex.InMemoryEntries is null)
                yield break;

            int safeCount = (int)Math.Min(_heapIndex.ObjectCount, _heapIndex.InMemoryEntries.Length);
            HeapEntry[] arr = _heapIndex.InMemoryEntries!;
            for (int i = 0; i < safeCount; i++)
                yield return arr[i];

            yield break;
        }

        foreach (HeapEntry entry in ObjectIndexReader.Instance.ReadEntries(_heapIndex.IndexPath))
            yield return entry;
    }

    public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
    {
        foreach (HeapEntry entry in EnumerateIndexedEntries())
            yield return (entry.Address, entry.MethodTable, entry.Size);
    }

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

    private static HeapIndexPrebuildMode SelectPrebuildMode(HeapIndexPrebuildMode requestedMode, string dumpPath)
    {
        if (requestedMode != HeapIndexPrebuildMode.Auto)
            return requestedMode;

        try
        {
            long dumpBytes = new FileInfo(dumpPath).Length;
            return dumpBytes <= 4096L * 1024 * 1024
                ? HeapIndexPrebuildMode.Memory
                : HeapIndexPrebuildMode.Disk;
        }
        catch
        {
            return HeapIndexPrebuildMode.Disk;
        }
    }
}
