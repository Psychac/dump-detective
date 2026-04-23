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
            if (selectedMode == HeapIndexPrebuildMode.Memory)
            {
                var memoryWriter = new MemoryBackedObjectIndexWriter();
                _heapIndex = memoryWriter.Build(heap, cancellationToken, progress);
                return _heapIndex;
            }

            var diskWriter = new DiskBackedObjectIndexWriter();
            _heapIndex = diskWriter.Build(heap, dumpPath, cancellationToken, progress);
            return _heapIndex;
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
            if (_staticRootedAddresses != null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _staticRootedAddresses;
            }

            Interlocked.Increment(ref _cacheMisses);

            _staticRootedAddresses = new HashSet<ulong>();

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                // OPT-#11: Plain increment in single-threaded scan loop; Interlocked fence unnecessary here.
                long scans = ++_objectScanCount;
                ReportProgress("Static root walk", scans);
                if (root.RootKind.ToString().Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                {
                    if (root.Object.IsValid)
                    {
                        _staticRootedAddresses.Add(root.Object.Address);
                    }
                }
            }

            return _staticRootedAddresses;
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
            var scanCounter = new ObjectScanCounter("Type statistics scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();
                // OPT-#11: Plain increment in single-threaded scan loop.
                long scans = ++_objectScanCount;
                ReportProgress("Type statistics scan", scans);

                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                ulong size = obj.Size;
                bool isLoh = size >= 85000;

                if (!_typeStats.TryGetValue(typeName, out var stats))
                {
                    stats = new CachedTypeStatistics { TypeName = typeName };
                    _typeStats[typeName] = stats;
                    _sampleInstances[typeName] = obj.Address; // Cache first instance
                }

                stats.Count++;
                stats.TotalSize += size;

                if (isLoh)
                {
                    stats.LohCount++;
                    stats.LohSize += size;
                }
            }

            scanCounter.Complete();

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

            var roots = new List<(string RootKind, ulong Address)>(capacity: 1024);
            var scanCounter = new ObjectScanCounter("Root enumeration", reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();
                ++_objectScanCount; // OPT-#11

                ulong address = root.Object.Address;
                if (address == 0)
                    continue;

                roots.Add((root.RootKind.ToString(), address));
            }

            scanCounter.Complete();
            _validRoots = roots;
            return _validRoots;
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



