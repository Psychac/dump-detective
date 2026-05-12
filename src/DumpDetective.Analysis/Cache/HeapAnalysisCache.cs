using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Cache
{
    internal class HeapAnalysisCache : IHeapAnalysisCache, IHeapIndexBuilder
    {
        private const int ProgressReportEveryScans = 25_000;
        private const long MemoryIndexDumpSizeThresholdBytes = 4096L * 1024 * 1024; // TEMP-ADAPTIVE-INDEXING: tune threshold with profiling.

        private HashSet<ulong>? _staticRootedAddresses;
        private Dictionary<string, CachedTypeStatistics>? _typeStats;
        private Dictionary<string, ulong>? _sampleInstances;
        private HeapIndexBuildResult? _heapIndex;
        private IReadOnlyList<(string RootKind, ulong Address)>? _validRoots;
        // Full root.ToString() descriptions keyed by object address, used by FindStaticRootOnlyEventLeaks
        // to parse publisher type and field name (e.g. "Static var MyClass._myEvent").
        private Dictionary<ulong, string>? _rootDescriptions;

        private long _objectScanCount;
        private long _cacheHits;
        private long _cacheMisses;
        private IProgress<AnalyzerProgressReport>? _progress;
        private DumpDetective.Core.Models.DumpSizeTier _sizeTier = DumpDetective.Core.Models.DumpSizeTier.Medium;
        private Dictionary<ulong, bool>? _methodTableHasRefs;
        private Dictionary<(ulong ThreadAddress, int MaxStackRootsToCount), int>? _threadStackRootCountCache;

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
                // Limit to ObjectCount to guard against any residual over-allocation from
                // the pre-alloc + atomic-cursor strategy in MemoryBackedObjectIndexWriter.
                int safeCount = (int)Math.Min(_heapIndex.ObjectCount, _heapIndex.InMemoryEntries.Length);
                HeapEntry[] arr = _heapIndex.InMemoryEntries;
                for (int i = 0; i < safeCount; i++)
                    yield return arr[i];

                yield break;
            }

            foreach (HeapEntry entry in ObjectIndexReader.Instance.ReadEntries(_heapIndex.IndexPath))
                yield return entry;
        }

        public IEnumerable<(ulong Address, ulong MethodTable, ulong Size)> EnumerateIndexedEntriesAsTuples()
        {
            // OPT-#15 (PERF-MED-01): yield return avoids the SelectEnumerableIterator + delegate closure
            // allocated by LINQ .Select() on every call. HeapEntry is a 24-byte struct; the projection
            // cost is identical — only the wrapper allocation is eliminated.
            foreach (HeapEntry entry in EnumerateIndexedEntries())
                yield return (entry.Address, entry.MethodTable, entry.Size);
        }

        public void SetProgress(IProgress<AnalyzerProgressReport>? progress)
        {
            _progress = progress;
        }

        public HeapIndexBuildResult PrebuildHeapIndex(
            ClrHeap heap,
            string dumpPath,
            CancellationToken cancellationToken,
            IProgress<AnalyzerProgressReport>? progress = null,
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
            IObjectIndexWriter writer = selectedMode == HeapIndexPrebuildMode.Memory
                ? new MemoryBackedObjectIndexWriter()
                : new DiskBackedObjectIndexWriter();

            _heapIndex = writer.Build(heap, cancellationToken, progress, dumpPath, _sizeTier);
            return _heapIndex;
        }

        public DumpDetective.Core.Models.DumpSizeTier SizeTier => _sizeTier;

        public int GetOrCountThreadStackRoots(ClrThread thread, int maxStackRootsToCount)
        {
            if (thread.Address == 0 || maxStackRootsToCount <= 0)
                return 0;

            _threadStackRootCountCache ??= new Dictionary<(ulong ThreadAddress, int MaxStackRootsToCount), int>(capacity: 256);
            var key = (thread.Address, maxStackRootsToCount);
            if (_threadStackRootCountCache.TryGetValue(key, out int cachedCount))
            {
                Interlocked.Increment(ref _cacheHits);
                return cachedCount;
            }

            Interlocked.Increment(ref _cacheMisses);

            int count = 0;
            foreach (var _ in thread.EnumerateStackRoots())
            {
                if (count >= maxStackRootsToCount)
                    break;
                count++;
            }

            _threadStackRootCountCache[key] = count;
            return count;
        }

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

                        if (string.IsNullOrEmpty(stats.ModuleName) && obj.Type.Module?.Name is string moduleName)
                            stats.ModuleName = System.IO.Path.GetFileName(moduleName);

                        stats.Count++;
                        stats.TotalSize += size;
                        if (isLoh)
                        {
                            stats.LohCount++;
                            stats.LohSize += size;
                        }

                        long s = Interlocked.Increment(ref totalScanned);
                        // OPT-#18 (PERF-MED-08): Do NOT call _progress.Report from parallel worker threads.
                        // Progress<T>.Report() without a SynchronizationContext dispatches on the calling
                        // thread, causing concurrent calls to race on any handler-side shared state.
                        // Progress is reported once in the sequential merge phase below.
                    }
                    return localState;
                },
                localState =>
                {
                    threadLocalResults.Add(localState);
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
            _progress?.Report(new AnalyzerProgressReport(totalScanned, "building type statistics"));

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
            if (_validRoots is not null)
            {
                Interlocked.Increment(ref _cacheHits);
                return _validRoots;
            }

            Interlocked.Increment(ref _cacheMisses);

            // Prefer disk-backed RootIndex when an index exists and is on-disk.
            if (_heapIndex is not null && _heapIndex.StorageKind == HeapIndexStorageKind.Disk)
            {
                try
                {
                    string rootIndexPath = DumpDetective.Analysis.Indexing.DumpIndexPaths.RootIndex(_heapIndex.IndexPath);
                    if (File.Exists(rootIndexPath))
                    {
                        var roots = ReadRootsFromIndex(rootIndexPath);
                        _validRoots = roots;
                        // Populate static-root set based on kind names containing "Static" (same heuristic as EnsureRootCaches)
                        _staticRootedAddresses ??= new HashSet<ulong>(capacity: Math.Max(256, roots.Count));
                        foreach (var (kind, addr) in roots)
                        {
                            if (kind.Contains(StringConstants.StaticPattern, StringComparison.OrdinalIgnoreCase))
                                _staticRootedAddresses.Add(addr);
                        }

                        Interlocked.Increment(ref _cacheHits);
                        return _validRoots;
                    }
                }
                catch
                {
                    // Fall back to in-memory enumeration on any read error.
                }
            }

            // Fall back to building via ClrMD enumeration (legacy behavior)
            EnsureRootCaches(heap);
            return _validRoots ?? Array.Empty<(string RootKind, ulong Address)>();
        }

        private static List<(string RootKind, ulong Address)> ReadRootsFromIndex(string rootIndexPath)
        {
            const int RootRecordSize = 20; // TargetAddr(8) | RootAddr(8) | Kind(1) | Pad(3)
            const int RootHeaderMagic = 0x58495452;
            const int RootHeaderVersion = 1;

            var roots = new List<(string, ulong)>();
            using FileStream fs = new(rootIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 256 * 1024, FileOptions.SequentialScan);

            Span<byte> headerBuf = stackalloc byte[24];
            if (fs.Read(headerBuf) < 24)
                return roots;

            int magic = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(headerBuf);
            int version = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(headerBuf[4..]);
            if (magic != RootHeaderMagic || version != RootHeaderVersion)
                return roots;

            long recordCount = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(headerBuf[8..]);
            if (recordCount <= 0)
                return roots;

            byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(RootRecordSize * 4096);
            try
            {
                int bytesRead;
                while ((bytesRead = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    int records = bytesRead / RootRecordSize;
                    for (int i = 0; i < records; i++)
                    {
                        int off = i * RootRecordSize;
                        ulong target = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off));
                        // ulong rootA  = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(off + 8));
                        byte kind = buf[off + 16];
                        string kindStr = kind switch
                        {
                            0 => "None",
                            1 => "FinalizerQueue",
                            2 => "StrongHandle",
                            3 => "PinnedHandle",
                            4 => "Stack",
                            5 => "RefCountedHandle",
                            6 => "AsyncPinnedHandle",
                            7 => "SizedRefHandle",
                            _ => $"Unknown({kind})"
                        };
                        roots.Add((kindStr, target));
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }

            return roots;
        }

        private void EnsureRootCaches(ClrHeap heap)
        {
            if (_staticRootedAddresses is not null && _validRoots is not null)
                return;

            // Initialize if needed with a reasonable capacity to avoid repeated resizes
            _staticRootedAddresses ??= new HashSet<ulong>(capacity: 4096);
            var roots = new List<(string RootKind, ulong Address)>(capacity: 4096);
            _rootDescriptions ??= new Dictionary<ulong, string>(capacity: 4096);

            var scanCounter = new ObjectScanCounter("enumerating roots", _progress, reportEveryObjects: 10_000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                scanCounter.Tick();
                // OPT-#11: Plain increment in single-threaded scan loop; Interlocked fence unnecessary here.
                ++_objectScanCount;

                ulong address = root.Object.Address;
                if (address == 0)
                    continue;

                string kind = root.RootKind.ToString();
                roots.Add((kind, address));

                // Store full description separately so ParseRootPublisher can extract TypeName.field.
                string description = root.ToString() ?? kind;
                _rootDescriptions.TryAdd(address, description);

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



