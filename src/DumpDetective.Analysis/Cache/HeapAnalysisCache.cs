using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Readers;
using DumpDetective.Core.Enums;
using System.Linq;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache, IHeapIndexBuilder
    {
        private const int ProgressReportEveryScans = 25_000;
        private const long MemoryIndexDumpSizeThresholdBytes = 4096L * 1024 * 1024; // TEMP-ADAPTIVE-INDEXING: tune threshold with profiling.

        private HashSet<ulong>? _staticRootedAddresses;
        private IReadOnlyList<(string RootKind, ulong Address)>? _validRoots;
        // Full root.ToString() descriptions keyed by object address, used by FindStaticRootOnlyEventLeaks
        // to parse publisher type and field name (e.g. "Static var MyClass._myEvent").
        private Dictionary<ulong, string>? _rootDescriptions;

        private long _objectScanCount;
        private long _cacheHits;
        private long _cacheMisses;
        private IProgress<AnalyzerProgressReport>? _progress;
        private DumpSizeTier _sizeTier = DumpSizeTier.Medium;
        private Dictionary<ulong, bool>? _methodTableHasRefs;
        private readonly MethodTableCache _methodTableCache;
        private readonly TypeMetadataCache _typeMetadataCache;
        private readonly ThreadCache _threadCache;

        private readonly HeapIndexCache _heapIndexCache = new HeapIndexCache();
        // Backwards-compatibility private field used by unit tests that inject a
        // prebuilt `HeapIndexBuildResult` via reflection. When non-null this value
        // is preferred by `TryGetHeapIndex`.
        private HeapIndexBuildResult? _heapIndex;
        private readonly StatisticsCache _statisticsCache;
        private readonly RootCache _rootCache;

        public long ObjectScanCount => Interlocked.Read(ref _objectScanCount);
        public long CacheHits => Interlocked.Read(ref _cacheHits);
        public long CacheMisses => Interlocked.Read(ref _cacheMisses);

        public HeapAnalysisCache()
        {
            _statisticsCache = new StatisticsCache(() =>
            {
                _heapIndexCache.TryGetHeapIndex(out var h);
                return h;
            });
            _rootCache = new RootCache(() =>
            {
                _heapIndexCache.TryGetHeapIndex(out var h);
                return h;
            });
            _threadCache = new ThreadCache();
            _methodTableCache = new MethodTableCache(() =>
            {
                _heapIndexCache.TryGetHeapIndex(out var h);
                return h;
            });
            _typeMetadataCache = new TypeMetadataCache(() =>
            {
                _heapIndexCache.TryGetHeapIndex(out var h);
                return h;
            }, _methodTableCache);
        }

        public IEnumerable<CacheMetrics> GetCacheMetrics()
        {
            yield return _heapIndexCache.GetMetrics();
            yield return _statisticsCache.GetMetrics();
            yield return _rootCache.GetMetrics();
            yield return _threadCache.GetMetrics();
            yield return _methodTableCache.GetMetrics();
            yield return _typeMetadataCache.GetMetrics();
        }

        public HeapCacheHealth GetHealth()
        {
            var metrics = GetCacheMetrics().ToList();
            bool overall = metrics.All(m => m.IsHealthy);
            return new HeapCacheHealth
            {
                Caches = metrics,
                OverallHealthy = overall,
                CheckedAt = DateTime.UtcNow
            };
        }

        public bool TryGetHeapIndex([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HeapIndexBuildResult? heapIndex)
        {
            if (_heapIndex is not null)
            {
                heapIndex = _heapIndex;
                return true;
            }

            return _heapIndexCache.TryGetHeapIndex(out heapIndex);
        }

        public IEnumerable<HeapEntry> EnumerateIndexedEntries() => _heapIndexCache.EnumerateIndexedEntries();

        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
        {
            foreach (var t in _heapIndexCache.EnumerateIndexedEntriesAsTuples())
                yield return t;
        }

        public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
        {
            _progress = progress;
            _heapIndexCache.SetProgress(progress);
            _statisticsCache.SetProgress(progress);
            _rootCache.SetProgress(progress);
        }

        public HeapIndexBuildResult PrebuildHeapIndex(
            ClrHeap heap,
            string dumpPath,
            CancellationToken cancellationToken,
            IProgress<AnalyzerProgressReport>? progress = null,
            HeapIndexPrebuildMode mode = HeapIndexPrebuildMode.Auto)
        {
            Interlocked.Increment(ref _cacheMisses);
            var result = _heapIndexCache.PrebuildHeapIndex(heap, dumpPath, cancellationToken, progress, mode);
            return result;
        }

        public DumpSizeTier SizeTier => _heapIndexCache.SizeTier;

        public int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount)
        {
            return _threadCache.GetOrCountThreadStackRoots(thread, maxStackRootsToCount);
        }

        public bool MethodTableHasOutgoingRefs(ClrHeap heap, ulong methodTable)
        {
            return _typeMetadataCache.MethodTableHasOutgoingRefs(heap, methodTable);
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
            return _rootCache.GetStaticRootedAddresses(heap);
        }

        public Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap)
        {
            return _statisticsCache.GetOrBuildTypeStatistics(heap);
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

                if (string.IsNullOrEmpty(stats.ModuleName))
                    stats.ModuleName = ResolveModuleNameFromSample(heap, aggregate.SampleAddress, methodTable);

                stats.Count = AddClamped(stats.Count, aggregate.Count);
                stats.TotalSize += aggregate.TotalSize;
                stats.LohCount = AddClamped(stats.LohCount, aggregate.LohCount);
                stats.LohSize += aggregate.LohSize;
            }

            return hydratedStats.Count > 0;
        }

        private static string ResolveTypeNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable)
        {
            // OPT-#19 (PERF-HIGH-06): GetTypeByMethodTable uses already-loaded type metadata and does
            // not touch object memory — no page fault into the dump file. Fall back to GetObject only
            // if the method-table lookup fails (e.g. corrupted / unknown MT).
            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            if (type?.Name is string name)
                return name;

            if (sampleAddress != 0)
            {
                ClrObject sample = heap.GetObject(sampleAddress);
                if (sample.IsValid && sample.Type?.Name is string sampleName)
                    return sampleName;
            }

            return $"MethodTable@0x{methodTable:X}";
        }

        private static string ResolveModuleNameFromSample(ClrHeap heap, ulong sampleAddress, ulong methodTable)
        {
            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            if (type?.Module?.Name is string moduleName && !string.IsNullOrWhiteSpace(moduleName))
                return System.IO.Path.GetFileName(moduleName);

            if (sampleAddress != 0)
            {
                ClrObject sample = heap.GetObject(sampleAddress);
                if (sample.IsValid && sample.Type?.Module?.Name is string sampleModuleName && !string.IsNullOrWhiteSpace(sampleModuleName))
                    return System.IO.Path.GetFileName(sampleModuleName);
            }

            return "N/A";
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
            return _statisticsCache.GetSampleInstanceAddress(typeName);
        }

        public HashSet<ulong> GetRetainedObjects(ClrHeap heap, ulong rootAddress, int maxObjects = 10000)
        {
            // Not cached: each root address is visited exactly once per analyzer run (StaticRootLeakDetector),
            // so a cache would only ever miss — storing up to maxObjects×8 bytes per root address for no benefit.
            Interlocked.Increment(ref _objectScanCount);

            var retained = new HashSet<ulong>(capacity: Math.Min(1000, maxObjects));
            var queue = new Queue<ulong>(capacity: 256);
            var scanCounter = new ObjectScanCounter("tracing retained objects", _progress, reportEveryObjects: 500);

            queue.Enqueue(rootAddress);
            retained.Add(rootAddress);

            while (queue.Count > 0 && retained.Count < maxObjects)
            {
                var current = queue.Dequeue();
                scanCounter.Tick();
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

        public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
        {
            return _rootCache.GetOrBuildValidRoots(heap);
        }

        // Root enumeration moved into RootCache

        public string? GetRootDescription(ulong address)
        {
            if (_rootDescriptions is null)
                return null;

            _rootDescriptions.TryGetValue(address, out string? desc);
            return desc;
        }

        private void ReportProgress(string phase, long totalScans)
        {
            if (_progress is null || totalScans % ProgressReportEveryScans != 0)
                return;

            _progress.Report(new AnalyzerProgressReport(totalScans, phase));
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



