using Microsoft.Diagnostics.Runtime;
using DumpDetective;

namespace DumpDetective.Utilities
{
    internal class HeapAnalysisCache
    {
        private HashSet<ulong>? _staticRootedAddresses;
        private Dictionary<string, TypeStatistics>? _typeStats;
        private Dictionary<string, ulong>? _sampleInstances;

        public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
        {
            if (_staticRootedAddresses != null)
                return _staticRootedAddresses;

            _staticRootedAddresses = new HashSet<ulong>();

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
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

        public Dictionary<string, TypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap)
        {
            if (_typeStats != null)
                return _typeStats;

            _typeStats = new Dictionary<string, TypeStatistics>(capacity: 1024);
            _sampleInstances = new Dictionary<string, ulong>(capacity: 1024);

            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null)
                    continue;

                string typeName = obj.Type.Name ?? StringConstants.UnknownType;
                ulong size = obj.Size;
                bool isLoh = size >= 85000;

                if (!_typeStats.TryGetValue(typeName, out var stats))
                {
                    stats = new TypeStatistics { TypeName = typeName };
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

            return _typeStats;
        }

        public ulong? GetSampleInstanceAddress(string typeName)
        {
            if (_sampleInstances != null && _sampleInstances.TryGetValue(typeName, out var address))
                return address;
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

    internal class TypeStatistics
    {
        public string TypeName { get; set; } = string.Empty;
        public int Count { get; set; }
        public ulong TotalSize { get; set; }
        public int LohCount { get; set; }
        public ulong LohSize { get; set; }
    }
}

