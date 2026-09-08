using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Analysis.Indexing.ForwardIndex;
using DumpDetective.Analysis.Indexing.Columns;
using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Core.Enums;
using System.Linq;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache, IHeapIndexBuilder, IDisposable
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
        private readonly SegmentSummaryCache _segmentSummaryCache = new();
        private readonly RootSetCache _rootSetCache;
        // All five share one implementation (§ 4) and one container session (§ 6.1) — they used to
        // be five near-identical classes each opening their own CacheContainerReader.
        private readonly LazyContainerIndex<IBackwardReferenceProvider> _reverseIndexCache;
        private readonly LazyContainerIndex<IForwardReferenceProvider> _forwardIndexCache;
        private readonly LazyContainerIndex<IReachableAddressProvider> _dominatorReachableIndexCache;
        private readonly LazyContainerIndex<IDominatorTreeProvider> _dominatorTreeCache;
        private readonly LazyContainerIndex<IThreadRetentionProvider> _threadRetentionCache;

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
            Func<CacheContainerReader?> container = () => _heapIndexCache.GetOrOpenContainerSession();

            _reverseIndexCache = new LazyContainerIndex<IBackwardReferenceProvider>(
                "ReverseIndexCache", container,
                c => ReverseEdgeIndexReader.TryOpen(c, out ReverseEdgeIndexReader? r) && r is not null
                    ? (new ReverseIndexBackwardReferenceProvider(r), r)
                    : (null, null));

            _forwardIndexCache = new LazyContainerIndex<IForwardReferenceProvider>(
                "ForwardIndexCache", container,
                c => ForwardEdgeIndexReader.TryOpen(c, out ForwardEdgeIndexReader? r) && r is not null
                    ? (new ForwardIndexForwardReferenceProvider(r), r)
                    : (null, null));

            _dominatorReachableIndexCache = new LazyContainerIndex<IReachableAddressProvider>(
                "DominatorReachableIndexCache", container,
                c => DominatorReachableAddressReader.TryOpen(c, out DominatorReachableAddressReader? r) && r is not null
                    ? (r, r)
                    : (null, null));

            _dominatorTreeCache = new LazyContainerIndex<IDominatorTreeProvider>(
                "DominatorTreeIndexCache", container,
                c => DominatorTreeReaderProvider.TryOpen(c, out DominatorTreeReaderProvider? p) && p is not null
                    ? (p, p)
                    : (null, null));

            // Threads' retention view is derived from the dominator tree, so it stays null whenever
            // that provider is unavailable — same dependency the dedicated class expressed via a
            // second constructor delegate.
            _threadRetentionCache = new LazyContainerIndex<IThreadRetentionProvider>(
                "ThreadRetentionIndexCache", container,
                c =>
                {
                    IDominatorTreeProvider? tree = _dominatorTreeCache.TryGetProvider();
                    if (tree is null)
                        return (null, null);

                    return ThreadRetentionReaderProvider.TryOpen(c, tree, CancellationToken.None,
                        out ThreadRetentionReaderProvider? p) && p is not null
                        ? (p, null)
                        : (null, null);
                });
        }

        public IEnumerable<CacheMetrics> GetCacheMetrics()
        {
            yield return _heapIndexCache.GetMetrics();
            yield return _statisticsCache.GetMetrics();
            yield return _rootSetCache.GetMetrics();
            yield return _threadCache.GetMetrics();
            yield return _methodTableCache.GetMetrics();
            yield return _typeMetadataCache.GetMetrics();
            yield return _reverseIndexCache.GetMetrics();
            yield return _forwardIndexCache.GetMetrics();
            yield return _dominatorReachableIndexCache.GetMetrics();
            yield return _dominatorTreeCache.GetMetrics();
            yield return _threadRetentionCache.GetMetrics();
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

        public long[]? TryGetGlobalSizeBuckets() =>
            TryGetHeapIndex(out HeapIndexBuildResult? heapIndex) ? heapIndex.GlobalSizeBuckets : null;

        // Read once per run and remembered, including the failure: the section is tens of KB and
        // several analyzers want it, so re-reading per caller would reintroduce exactly the
        // redundancy the container session exists to remove.
        private IReadOnlyList<ulong>? _distinctMethodTables;
        private bool _distinctMethodTablesAttempted;

        public IReadOnlyList<ulong>? TryGetDistinctMethodTables()
        {
            if (_distinctMethodTablesAttempted)
                return _distinctMethodTables;

            _distinctMethodTablesAttempted = true;

            CacheContainerReader? container = _heapIndexCache.GetOrOpenContainerSession();
            if (container is not null
                && ObjectColumnSet.TryLoadTypeDictionary(container, out ulong[]? methodTables)
                && methodTables is not null)
            {
                _distinctMethodTables = methodTables;
            }

            return _distinctMethodTables;
        }

        public IEnumerable<HeapEntry> EnumerateIndexedEntries() => _heapIndexCache.EnumerateIndexedEntries();

        public IEnumerable<HeapEntry> EnumerateIndexedEntriesRange(long startRecord, long recordCount) =>
            _heapIndexCache.EnumerateIndexedEntriesRange(startRecord, recordCount);

        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
        {
            foreach (var t in _heapIndexCache.EnumerateIndexedEntriesAsTuples())
                yield return t;
        }

        public bool TryGetObjectMetadata(ClrHeap heap, ulong address, out ulong methodTable, out ulong size)
        {
            return _heapIndexCache.TryGetObjectMetadata(heap, address, out methodTable, out size);
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
            IProgress<AnalyzerProgressReport>? progress = null,
            IReadOnlyList<IAnalyzer>? activeAnalyzers = null,
            bool enableExactDominatorTree = false)
        {
            Interlocked.Increment(ref _cacheMisses);
            var result = _heapIndexCache.PrebuildHeapIndex(heap, dumpPath, cancellationToken, progress, activeAnalyzers, enableExactDominatorTree);
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

        public HashSet<ulong> GetPinnedRootedAddresses(ClrHeap heap)
        {
            return _rootSetCache.GetPinnedRootedAddresses(heap);
        }

        public Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)> GetStaticFieldsByRootAddress(ClrHeap heap)
        {
            return _rootSetCache.GetStaticFieldsByRootAddress(heap);
        }

        public bool TryResolveStackFrameOwner(ClrHeap heap, ulong rootAddr, out string ownerType, out string methodName)
        {
            return _rootSetCache.TryResolveStackFrameOwner(heap, rootAddr, out ownerType, out methodName);
        }

        public Dictionary<string, CachedTypeStatistics> GetOrBuildTypeStatistics(ClrHeap heap)
        {
            return _statisticsCache.GetOrBuildTypeStatistics(heap);
        }

        public IReadOnlyList<SegmentSummary> GetOrBuildSegmentSummaries(ClrHeap heap)
        {
            return _segmentSummaryCache.GetOrBuild(heap);
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
            // Reuse names StatisticsCache already resolved for this exact MethodTable population
            // (GCGenerationAnalyzer always warms it before this is called) instead of re-touching
            // ClrMD/DAC for every type.
            if (_statisticsCache.TryGetTypeName(methodTable, out typeName))
                return true;

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

        public IReadOnlyList<(string RootKind, ulong TargetAddr, ulong RootAddr)> GetOrBuildRootTriples(ClrHeap heap)
        {
            return _rootSetCache.GetOrBuildRootTriples(heap);
        }

        public IReadOnlyList<RootRecord> GetOrBuildRoots(ClrHeap heap, CancellationToken cancellationToken = default)
        {
            return _rootSetCache.GetOrBuildRoots(heap, cancellationToken);
        }

        // Root enumeration moved into RootSetCache

        public IBackwardReferenceProvider? TryGetReverseIndexProvider()
        {
            return _reverseIndexCache.TryGetProvider();
        }

        public IForwardReferenceProvider? TryGetForwardIndexProvider()
        {
            return _forwardIndexCache.TryGetProvider();
        }

        public IReachableAddressProvider? TryGetReachableAddressProvider()
        {
            return _dominatorReachableIndexCache.TryGetProvider();
        }

        public IDominatorTreeProvider? TryGetDominatorTreeProvider()
        {
            return _dominatorTreeCache.TryGetProvider();
        }

        public IThreadRetentionProvider? TryGetThreadRetentionProvider()
        {
            return _threadRetentionCache.TryGetProvider();
        }

        private void ReportProgress(string phase, long totalScans)
        {
            if (_progress is null || totalScans % ProgressReportEveryScans != 0)
                return;

            _progress.Report(new AnalyzerProgressReport(totalScans, phase));
        }

        public void Dispose()
        {
            _reverseIndexCache.Dispose();
            _forwardIndexCache.Dispose();
            _dominatorReachableIndexCache.Dispose();
            _dominatorTreeCache.Dispose();
            // Owns nothing today (the provider holds no unmanaged handle), but it is IDisposable
            // like its four siblings — disposing it keeps that from becoming a silent leak later.
            _threadRetentionCache.Dispose();
            _heapIndexCache.Dispose();

            if (Indexing.Container.CacheContainerReader.PerfLogSession)
                Console.Error.WriteLine(Indexing.Container.CacheContainerReader.PerfSummary());
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



