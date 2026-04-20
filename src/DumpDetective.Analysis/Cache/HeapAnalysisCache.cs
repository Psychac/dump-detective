using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache
    {
        private HashSet<ulong>? _staticRootedAddresses;
        private Dictionary<string, CachedTypeStatistics>? _typeStats;
        private Dictionary<string, ulong>? _sampleInstances;

        private long _objectScanCount;
        private long _cacheHits;
        private long _cacheMisses;

        public long ObjectScanCount => Interlocked.Read(ref _objectScanCount);
        public long CacheHits => Interlocked.Read(ref _cacheHits);
        public long CacheMisses => Interlocked.Read(ref _cacheMisses);

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
                Interlocked.Increment(ref _objectScanCount);
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

            Interlocked.Increment(ref _cacheMisses);

            _typeStats = new Dictionary<string, CachedTypeStatistics>(capacity: 1024);
            _sampleInstances = new Dictionary<string, ulong>(capacity: 1024);
            var scanCounter = new ObjectScanCounter("Type statistics scan");

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();
                Interlocked.Increment(ref _objectScanCount);

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
                Interlocked.Increment(ref _objectScanCount);
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



