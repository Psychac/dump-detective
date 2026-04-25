using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache
    {
        private const int ProgressReportEveryScans = 25_000;
        private const long MemoryIndexDumpSizeThresholdBytes = 4096L * 1024 * 1024; // TEMP-ADAPTIVE-INDEXING: tune threshold with profiling.

        private HashSet<ulong>? _staticRootedAddresses;
        private Dictionary<string, CachedTypeStatistics>? _typeStats;
        private Dictionary<string, ulong>? _sampleInstances;
        private HeapIndexBuildResult? _heapIndex;
        private IReadOnlyList<(string RootKind, ulong Address)>? _validRoots;

        private long _objectScanCount;
        private long _cacheHits;
        private long _cacheMisses;
        private Action<string, long>? _progressReporter;
        private DumpDetective.Core.Models.DumpSizeTier _sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium;
        private Dictionary<ulong, HashSet<ulong>>? _retainedObjectsCache;
        private Dictionary<ulong, bool>? _methodTableHasRefs;

        public long ObjectScanCount => Interlocked.Read(ref _objectScanCount);
        public long CacheHits => Interlocked.Read(ref _cacheHits);
        public long CacheMisses => Interlocked.Read(ref _cacheMisses);

        public bool TryGetHeapIndex([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapIndexBuildResult? heapIndex)
        {
            heapIndex = _heapIndex;
            return heapIndex is not null;
        }

        public IEnumerable<HeapEntry> EnumerateIndexedEntries()
        {
            if (_heapIndex is null)
                yield break;

            if (_heapIndex.StorageKind == HeapIndexStorageKind.Memory)
            {
                if (_heapIndex.InMemoryEntries is null)
                    yield break;

                // OPT-#14: Iterate over HeapEntry[] directly (was IReadOnlyList<HeapEntry>).
                foreach (HeapEntry entry in _heapIndex.InMemoryEntries)
                    yield return entry;

                yield break;
            }

            foreach (HeapEntry entry in HeapIndexEntryReader.ReadDiskEntries(_heapIndex.IndexPath))
                yield return entry;
        }

        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
        {
            if (_heapIndex is null)
                yield break;

            if (_heapIndex.StorageKind == HeapIndexStorageKind.Memory)
            {
                if (_heapIndex.InMemoryEntries is null)
                    yield break;

                foreach (HeapEntry entry in _heapIndex.InMemoryEntries)
                    yield return (entry.Address, entry.MethodTable, entry.Size);

                yield break;
            }

            foreach (HeapEntry entry in HeapIndexEntryReader.ReadDiskEntries(_heapIndex.IndexPath))
                yield return (entry.Address, entry.MethodTable, entry.Size);
        }

        public void SetProgressReporter(Action<string, long>? progressReporter)
        {
            _progressReporter = progressReporter;
        }

        public HeapIndexBuildResult PrebuildHeapIndex(
            ClrHeap heap,
            string dumpPath,
            CancellationToken cancellationToken,
            Action<long, TimeSpan>? progress = null,
            HeapIndexPrebuildMode mode = HeapIndexPrebuildMode.Auto)
        {
            if (_heapIndex is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _heapIndex;
            }

            Interlocked.Increment(ref _cacheMisses);

            HeapIndexPrebuildMode selectedMode = SelectPrebuildMode(mode, dumpPath);
            // Determine dump size tier once and cache it for adaptive decisions
            try
            {
                long dumpBytes = new FileInfo(dumpPath).Length;
                _sizeTier = dumpBytes > 4L * 1024 * 1024 * 1024 ? DumpDetective.Core.Models.DumpSizeTier.Large :
                            dumpBytes > 512L * 1024 * 1024 ? DumpDetective.Core.Models.DumpSizeTier.Medium :
                            DumpDetective.Core.Models.DumpSizeTier.Small;
            }
            catch
            {
                _sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium;
            }
            if (selectedMode == HeapIndexPrebuildMode.Memory)
            {
                var memoryWriter = new MemoryBackedObjectIndexWriter();
                _heapIndex = memoryWriter.Build(heap, cancellationToken, progress);
                return _heapIndex;
            }

            var diskWriter = new DiskBackedObjectIndexWriter();
            _heapIndex = diskWriter.Build(heap, dumpPath, cancellationToken, progress, _sizeTier);
            return _heapIndex;
        }

        public DumpDetective.Core.Models.DumpSizeTier SizeTier => _sizeTier;

        public bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable)
        {
            if (methodTable == 0)
                return false;

            _methodTableHasRefs ??= new Dictionary<ulong, bool>(capacity: 512);
            if (_methodTableHasRefs.TryGetValue(methodTable, out var cached))
                return cached;

            // Fast path: if we have a prebuilt index, hydrate from the index sample address.
            if (_heapIndex?.TypeAggregates is IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates
                && aggregates.TryGetValue(methodTable, out var aggregate))
            {
                if (aggregate.SampleAddress != 0)
                {
                    try
                    {
                        ClrObject sample = heap.GetObject(aggregate.SampleAddress);
                        bool has = sample.IsValid && sample.Type is not null && sample.Type.ContainsPointers;
                        _methodTableHasRefs[methodTable] = has;
                        return has;
                    }
                    catch
                    {
                        // fallthrough to conservative default below
                    }
                }
            }

            // Fallback: ask ClrHeap for the type by method-table (fast) and inspect fields.
            try
            {
                ClrType? type = heap.GetTypeByMethodTable(methodTable);
                if (type is not null)
                {
                    bool has = false;
                    if (type.IsArray)
                    {
                        has = type.ComponentType?.IsObjectReference == true;
                    }
                    else
                    {
                        foreach (ClrInstanceField field in type.Fields)
                        {
                            if (field.IsObjectReference)
                            {
                                has = true;
                                break;
                            }
                        }
                    }

                    _methodTableHasRefs[methodTable] = has;
                    return has;
                }
            }
            catch
            {
                // ignore and fall through to conservative default
            }

            // Conservative default: assume method-table has outgoing refs to avoid missing referents.
            _methodTableHasRefs[methodTable] = true;
            return true;
        }

        private static HeapIndexPrebuildMode SelectPrebuildMode(HeapIndexPrebuildMode requestedMode, string dumpPath)
        {
            if (requestedMode != HeapIndexPrebuildMode.Auto)
                return requestedMode;

            try
            {
                long dumpBytes = new FileInfo(dumpPath).Length;
                return dumpBytes <= MemoryIndexDumpSizeThresholdBytes
                    ? HeapIndexPrebuildMode.Memory
                    : HeapIndexPrebuildMode.Disk;
            }
            catch
            {
                return HeapIndexPrebuildMode.Disk;
            }
        }

        public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
        {
            if (_staticRootedAddresses is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _staticRootedAddresses;
            }

            Interlocked.Increment(ref _cacheMisses);

            EnsureRootCaches(heap);
            return _staticRootedAddresses ?? new HashSet<ulong>();
        }

        public Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap)
        {
            if (_typeStats != null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _typeStats;
            }

            if (_heapIndex is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                if (TryHydrateTypeStatisticsFromIndex(heap, _heapIndex.TypeAggregates, out Dictionary<string, CachedTypeStatistics>? hydratedStats, out Dictionary<string, ulong>? hydratedSamples))
                {
                    _typeStats = hydratedStats;
                    _sampleInstances = hydratedSamples;
                    return _typeStats;
                }
            }

            Interlocked.Increment(ref _cacheMisses);

            _typeStats = new Dictionary<string, CachedTypeStatistics>(capacity: 1024);
            _sampleInstances = new Dictionary<string, ulong>(capacity: 1024);

            // Parallel segment walk — each thread builds a local dict, merged sequentially at the end.
            var threadLocalResults = new System.Collections.Concurrent.ConcurrentBag<
                (Dictionary<string, CachedTypeStatistics> Stats, Dictionary<string, ulong> Samples)>();
            long totalScanned = 0;

            Parallel.ForEach(
                heap.Segments,
                () => (Stats: new Dictionary<string, CachedTypeStatistics>(),
                       Samples: new Dictionary<string, ulong>()),
                (segment, _, localState) =>
                {
                    foreach (ClrObject obj in segment.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type == null)
                            continue;

                        string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                        ulong size = obj.Size;
                        bool isLoh = size >= 85000;

                        if (!localState.Stats.TryGetValue(typeName, out var stats))
                        {
                            stats = new CachedTypeStatistics { TypeName = typeName };
                            localState.Stats[typeName] = stats;
                            localState.Samples[typeName] = obj.Address;
                        }

                        stats.Count++;
                        stats.TotalSize += size;
                        if (isLoh)
                        {
                            stats.LohCount++;
                            stats.LohSize += size;
                        }
                    }
                    return localState;
                },
                localState =>
                {
                    threadLocalResults.Add(localState);
                    Interlocked.Add(ref totalScanned, localState.Stats.Values.Sum(s => (long)s.Count));
                });

            // Merge thread-local results into the shared cache (sequential, runs once).
            foreach (var (localStats, localSamples) in threadLocalResults)
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
            }

            Interlocked.Add(ref _objectScanCount, totalScanned);
            ReportProgress("Type statistics scan", _objectScanCount);

            return _typeStats;
        }

        private static bool TryHydrateTypeStatisticsFromIndex(
            ClrHeap heap,
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> typeAggregates,
            out Dictionary<string, CachedTypeStatistics> hydratedStats,
            out Dictionary<string, ulong> hydratedSamples)
        {
            hydratedStats = new Dictionary<string, CachedTypeStatistics>(Math.Max(1024, typeAggregates.Count));
            hydratedSamples = new Dictionary<string, ulong>(Math.Max(1024, typeAggregates.Count));

            foreach ((ulong methodTable, TypeAggregateIndexEntry aggregate) in typeAggregates)
            {
                string typeName = ResolveTypeNameFromSample(heap, aggregate.SampleAddress, methodTable);

                if (!hydratedStats.TryGetValue(typeName, out CachedTypeStatistics? stats))
                {
                    stats = new CachedTypeStatistics { TypeName = typeName };
                    hydratedStats[typeName] = stats;

                    if (aggregate.SampleAddress != 0)
                    {
                        hydratedSamples[typeName] = aggregate.SampleAddress;
                    }
                }

                stats.Count = AddClamped(stats.Count, aggregate.Count);
                stats.TotalSize += aggregate.TotalSize;
                stats.LohCount = AddClamped(stats.LohCount, aggregate.LohCount);
                stats.LohSize += aggregate.LohSize;
            }

            return hydratedStats.Count > 0;
        }

        private static string ResolveTypeNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable)
        {
            if (sampleAddress != 0)
            {
                ClrObject sample = heap.GetObject(sampleAddress);
                if (sample.IsValid && sample.Type != null)
                {
                    return sample.Type.Name ?? StringConstants.UnknownType;
                }
            }

            return $"MethodTable@0x{methodTable:X}";
        }

        private static int AddClamped(int existing, long delta)
        {
            if (delta <= 0)
                return existing;

            long result = existing + delta;
            return result >= int.MaxValue ? int.MaxValue : (int)result;
        }

        public ulong? GetSampleInstanceAddress(string typeName)
        {
            if (_sampleInstances != null && _sampleInstances.TryGetValue(typeName, out var address))
            {
                Interlocked.Increment(ref _cacheHits);
                return address;
            }

            Interlocked.Increment(ref _cacheMisses);
            return null;
        }

        public HashSet<ulong> GetRetainedObjects(ClrHeap heap, ulong rootAddress, int maxObjects = 10000)
        {
            // Check cache first
            if (_retainedObjectsCache != null && _retainedObjectsCache.TryGetValue(rootAddress, out var cached))
            {
                Interlocked.Increment(ref _cacheHits);
                return cached;
            }

            Interlocked.Increment(ref _cacheMisses);

            var retained = new HashSet<ulong>(capacity: Math.Min(1000, maxObjects));
            var queue = new Queue<ulong>(capacity: 256);

            queue.Enqueue(rootAddress);
            retained.Add(rootAddress);

            while (queue.Count > 0 && retained.Count < maxObjects)
            {
                var current = queue.Dequeue();
                long scans = ++_objectScanCount; // OPT-#11: plain increment, analysis is single-threaded
                ReportProgress("Retained graph walk", scans);
                var obj = heap.GetObject(current);

                if (!obj.IsValid)
                    continue;

                foreach (var reference in obj.EnumerateReferences(carefully: true))
                {
                    if (reference.IsValid && retained.Add(reference.Address))
                    {
                        queue.Enqueue(reference.Address);
                    }
                }
            }

            _retainedObjectsCache ??= new Dictionary<ulong, HashSet<ulong>>();
            _retainedObjectsCache[rootAddress] = retained;

            return retained;
        }

        public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
        {
            if (_validRoots is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _validRoots;
            }

            Interlocked.Increment(ref _cacheMisses);
            EnsureRootCaches(heap);
            return _validRoots ?? Array.Empty<(string RootKind, ulong Address)>();
        }

        private void EnsureRootCaches(ClrHeap heap)
        {
            if (_staticRootedAddresses is not null && _validRoots is not null)
                return;

            // Initialize if needed with a reasonable capacity to avoid repeated resizes
            _staticRootedAddresses ??= new HashSet<ulong>(capacity: 4096);
            var roots = new List<(string RootKind, ulong Address)>(capacity: 4096);

            var scanCounter = new ObjectScanCounter("Root enumeration", reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();
                // OPT-#11: Plain increment in single-threaded scan loop; Interlocked fence unnecessary here.
                ++_objectScanCount;
                ReportProgress("Static root walk", _objectScanCount);

                ulong address = root.Object.Address;
                if (address == 0)
                    continue;

                string kind = root.RootKind.ToString();
                roots.Add((kind, address));

                if (kind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase) && root.Object.IsValid)
                {
                    _staticRootedAddresses.Add(address);
                }
            }

            scanCounter.Complete();
            _validRoots = roots;
        }

        public string? GetRootDescription(ulong address)
        {
            if (_validRoots is null)
                return null;

            foreach (var (kind, addr) in _validRoots)
            {
                if (addr == address)
                    return kind;
            }

            return null;
        }

        private void ReportProgress(string operation, long totalScans)
        {
            Action<string, long>? reporter = _progressReporter;
            if (reporter is null)
            {
                return;
            }

            if (totalScans % ProgressReportEveryScans != 0)
            {
                return;
            }

            reporter(operation, totalScans);
        }
    }

    internal class TaskStatistics
    {
        public int TotalTasks { get; set; }
        public int PendingTasks { get; set; }
        public int FaultedTasks { get; set; }
        public int CanceledTasks { get; set; }
        public int QueuedWorkItems { get; set; }
        public bool TaskScanLimited { get; set; }
    }

}



