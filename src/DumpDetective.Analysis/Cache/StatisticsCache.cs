using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Models;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Cache;

internal class StatisticsCache
{
    private Dictionary<string, CachedTypeStatistics>? _typeStats;
    private Dictionary<string, ulong>? _sampleInstances;
    private Dictionary<ulong, string>? _typeNamesByMethodTable;

    private readonly Func<HeapIndexBuildResult?> _getHeapIndex;
    private IProgress<AnalyzerProgressReport>? _progress;
    private DateTime? _lastBuildTime;
    private TimeSpan? _lastBuildDuration;
    private string? _lastBuildError;

    public StatisticsCache(Func<HeapIndexBuildResult?> getHeapIndex)
    {
        _getHeapIndex = getHeapIndex ?? throw new ArgumentNullException(nameof(getHeapIndex));
    }

    public Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap)
    {
        if (heap is null)
            throw new ArgumentNullException(nameof(heap));

        if (_typeStats != null)
            return _typeStats;

        var __sw = System.Diagnostics.Stopwatch.StartNew();
        var heapIndex = _getHeapIndex();
        if (heapIndex is not null)
        {
            if (TryHydrateTypeStatisticsFromIndex(heap, heapIndex.TypeAggregates, out var hydratedStats, out var hydratedSamples, out var hydratedNamesByMethodTable))
            {
                //Console.Error.WriteLine($"[PERF] StatisticsCache: hydrated from index, {heapIndex.TypeAggregates.Count} unique MTs, {__sw.Elapsed.TotalSeconds:F2}s");
                _typeStats = hydratedStats;
                _sampleInstances = hydratedSamples;
                _typeNamesByMethodTable = hydratedNamesByMethodTable;
                return _typeStats;
            }
        }

        //Console.Error.WriteLine($"[PERF] StatisticsCache: FALLING BACK to full heap walk (hydration failed or no index)");
        _typeStats = new Dictionary<string, CachedTypeStatistics>(capacity: 1024);
        _sampleInstances = new Dictionary<string, ulong>(capacity: 1024);
        _typeNamesByMethodTable = new Dictionary<ulong, string>(capacity: 1024);

        var threadLocalResults = new System.Collections.Concurrent.ConcurrentBag<
            (Dictionary<string, CachedTypeStatistics> Stats, Dictionary<string, ulong> Samples, Dictionary<ulong, string> NamesByMethodTable)>();
        long totalScanned = 0;

        Parallel.ForEach(
            heap.Segments,
            () => (Stats: new Dictionary<string, CachedTypeStatistics>(),
                   Samples: new Dictionary<string, ulong>(),
                   NamesByMethodTable: new Dictionary<ulong, string>()),
            (segment, _, localState) =>
            {
                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.Type == null)
                        continue;

                    string typeName = obj.Type.Name ?? "<unknown>";
                    ulong size = obj.Size;
                    bool isLoh = size >= 85000;

                    if (!localState.Stats.TryGetValue(typeName, out var stats))
                    {
                        stats = new CachedTypeStatistics { TypeName = typeName };
                        localState.Stats[typeName] = stats;
                        localState.Samples[typeName] = obj.Address;
                        localState.NamesByMethodTable[obj.Type.MethodTable] = typeName;
                    }

                    if (string.IsNullOrEmpty(stats.ModuleName) && obj.Type.Module?.Name is string moduleName)
                        stats.ModuleName = System.IO.Path.GetFileName(moduleName);

                    stats.Count++;
                    stats.TotalSize += size;
                    if (isLoh)
                    {
                        stats.LohCount++;
                        stats.LohSize += size;
                    }

                    Interlocked.Increment(ref totalScanned);
                }
                return localState;
            },
            localState => threadLocalResults.Add(localState));

        foreach (var (localStats, localSamples, localNamesByMethodTable) in threadLocalResults)
        {
            foreach ((string typeName, CachedTypeStatistics localStat) in localStats)
            {
                if (!_typeStats.TryGetValue(typeName, out var stat))
                {
                    stat = new CachedTypeStatistics { TypeName = typeName };
                    _typeStats[typeName] = stat;
                    if (localSamples.TryGetValue(typeName, out ulong sample))
                        _sampleInstances[typeName] = sample;
                }

                stat.Count = AddClamped(stat.Count, localStat.Count);
                stat.TotalSize += localStat.TotalSize;
                stat.LohCount = AddClamped(stat.LohCount, localStat.LohCount);
                stat.LohSize += localStat.LohSize;
            }

            foreach ((ulong methodTable, string typeName) in localNamesByMethodTable)
                _typeNamesByMethodTable.TryAdd(methodTable, typeName);
        }

        _progress?.Report(new AnalyzerProgressReport(totalScanned, "building type statistics"));
        _lastBuildTime = DateTime.UtcNow;
        // lastBuildDuration not tracked for parallel aggregation currently
        _lastBuildDuration = TimeSpan.Zero;
        _lastBuildError = null;

        return _typeStats;
    }

    public ulong? GetSampleInstanceAddress(string typeName)
    {
        if (_sampleInstances != null && _sampleInstances.TryGetValue(typeName, out var address))
            return address;

        return null;
    }

    /// <summary>
    /// Returns a type name already resolved during <see cref="GetOrBuildTypeStatistics"/>, avoiding
    /// a fresh ClrMD/DAC round trip. Every MethodTable in TypeAggregates gets resolved exactly once
    /// here; other Phase-2 analyzers that need names for the same population (e.g.
    /// AllocationPatternAnalyzer) should reuse this instead of re-resolving via
    /// <c>heap.GetTypeByMethodTable</c>.
    /// </summary>
    public bool TryGetTypeName(ulong methodTable, out string? typeName)
    {
        if (_typeNamesByMethodTable != null && _typeNamesByMethodTable.TryGetValue(methodTable, out typeName))
            return true;

        typeName = null;
        return false;
    }

    public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
    {
        _progress = progress;
    }

    public CacheMetrics GetMetrics()
    {
        return new CacheMetrics
        {
            Name = nameof(StatisticsCache),
            LastBuildDurationMs = _lastBuildDuration?.TotalMilliseconds is double d ? (long?)d : null,
            LastBuildStatus = _lastBuildError is null ? "success" : "failure",
            EntryCount = _typeStats?.Count ?? 0,
            MemoryUsageBytes = 0,
            LastBuildTime = _lastBuildTime,
            IsHealthy = _lastBuildError is null,
            LastError = _lastBuildError
        };
    }

    private static bool TryHydrateTypeStatisticsFromIndex(
        ClrHeap heap,
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> typeAggregates,
        out Dictionary<string, CachedTypeStatistics> hydratedStats,
        out Dictionary<string, ulong> hydratedSamples,
        out Dictionary<ulong, string> hydratedNamesByMethodTable)
    {
        hydratedStats = new Dictionary<string, CachedTypeStatistics>(Math.Max(1024, typeAggregates.Count));
        hydratedSamples = new Dictionary<string, ulong>(Math.Max(1024, typeAggregates.Count));
        hydratedNamesByMethodTable = new Dictionary<ulong, string>(Math.Max(1024, typeAggregates.Count));

        foreach ((ulong methodTable, TypeAggregateIndexEntry aggregate) in typeAggregates)
        {
            string typeName = ResolveTypeNameFromSample(heap, aggregate.SampleAddress, methodTable);
            hydratedNamesByMethodTable[methodTable] = typeName;

            if (!hydratedStats.TryGetValue(typeName, out CachedTypeStatistics? stats))
            {
                stats = new CachedTypeStatistics { TypeName = typeName };
                hydratedStats[typeName] = stats;

                if (aggregate.SampleAddress != 0)
                {
                    hydratedSamples[typeName] = aggregate.SampleAddress;
                }
            }

            if (string.IsNullOrEmpty(stats.ModuleName))
                stats.ModuleName = ResolveModuleNameFromSample(heap, aggregate.SampleAddress, methodTable);

            stats.Count = AddClamped(stats.Count, aggregate.Count);
            stats.TotalSize += aggregate.TotalSize;
            stats.LohCount = AddClamped(stats.LohCount, aggregate.LohCount);
            stats.LohSize += aggregate.LohSize;
        }

        return hydratedStats.Count > 0;
    }

    private static string ResolveTypeNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable) =>
        TypeAggregateNameResolver.ResolveTypeName(heap, methodTable, sampleAddress);

    private static string ResolveModuleNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable) =>
        TypeAggregateNameResolver.ResolveModuleName(heap, methodTable, sampleAddress);

    private static int AddClamped(int existing, long delta)
    {
        if (delta <= 0)
            return existing;

        long result = existing + delta;
        return result >= int.MaxValue ? int.MaxValue : (int)result;
    }
}
