using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Enums;
using System.Linq;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache, IHeapIndexBuilder
    {
        private const int ProgressReportEveryScans = 25_000;

        private long _objectScanCount;
        private long _cacheHits;
        private long _cacheMisses;
        private IProgress<AnalyzerProgressReport>? _progress;
        private readonly MethodTableCache _methodTableCache;
        private readonly TypeMetadataCache _typeMetadataCache;
        private readonly ThreadCache _threadCache;

        private readonly HeapIndexCache _heapIndexCache = new HeapIndexCache();
        // Backwards-compatibility private field used by unit tests that inject a
        // prebuilt `HeapIndexBuildResult` via reflection. When non-null this value
        // is preferred by `TryGetHeapIndex`.
        private HeapIndexBuildResult? _heapIndex;
        private readonly StatisticsCache _statisticsCache;
        private readonly RootSetCache _rootSetCache;

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
            _rootSetCache = new RootSetCache(() =>
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
            yield return _rootSetCache.GetMetrics();
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

        public IEnumerable<HeapEntry> EnumerateIndexedEntriesRange(long startRecord, long recordCount) =>
            _heapIndexCache.EnumerateIndexedEntriesRange(startRecord, recordCount);

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
            _rootSetCache.SetProgress(progress);
        }

        public HeapIndexBuildResult PrebuildHeapIndex(
            ClrHeap heap,
            string dumpPath,
            CancellationToken cancellationToken,
            IProgress<AnalyzerProgressReport>? progress = null)
        {
            Interlocked.Increment(ref _cacheMisses);
            var result = _heapIndexCache.PrebuildHeapIndex(heap, dumpPath, cancellationToken, progress);
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

        public HashSet<ulong> GetStaticRootedAddresses(ClrHeap heap)
        {
            return _rootSetCache.GetStaticRootedAddresses(heap);
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

        public bool TryGetTypeName(ClrHeap heap, ulong methodTable, out string? typeName)
        {
            typeName = null;
            
            // Try TypeMetadataCache first to see if we've already extracted this type's metadata
            if (_typeMetadataCache.TryGet(methodTable, out _))
            {
                // We have metadata, so the type is known. Use GetTypeByMethodTable (it will be fast, already in CLR metadata cache)
                ClrType? type = heap.GetTypeByMethodTable(methodTable);
                if (type?.Name is string name)
                {
                    typeName = name;
                    return true;
                }
            }
            else
            {
                // No metadata cached yet, call GetTypeByMethodTable (may be expensive on first call)
                ClrType? type = heap.GetTypeByMethodTable(methodTable);
                if (type?.Name is string name)
                {
                    typeName = name;
                    // Populate the metadata cache for future lookups
                    _ = GetOrCreate(heap, methodTable);
                    return true;
                }
            }
            
            return false;
        }
        
        public TypeMetadata GetOrCreate(ClrHeap heap, ulong methodTable)
        {
            return _typeMetadataCache.GetOrCreate(heap, methodTable);
        }


        public IReadOnlyList<(string RootKind, ulong Address)> GetOrBuildValidRoots(ClrHeap heap)
        {
            return _rootSetCache.GetOrBuildValidRoots(heap);
        }

        public IReadOnlyList<RootRecord> GetOrBuildRoots(ClrHeap heap)
        {
            return _rootSetCache.GetOrBuildRoots(heap);
        }

        // Root enumeration moved into RootSetCache

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



