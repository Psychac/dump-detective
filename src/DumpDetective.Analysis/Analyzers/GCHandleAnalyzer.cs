using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    public sealed class GCHandleAnalyzer : IAnalyzer
    {
        public string Name => "GC Handle Analysis";
        public string Category => "Handles";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GCHandleAnalysisOptions options = context.AnalysisOptions.GCHandleAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, options, context.Progress, cancellationToken).Stamp(this));
        }

        public AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap = null, IHeapAnalysisCache? cache = null)
        {
            return Analyze(runtime, heap, cache, new GCHandleAnalysisOptions(), progress: null, CancellationToken.None);
        }

        private AnalyzerDomainResult Analyze(ClrRuntime runtime, ClrHeap? heap, IHeapAnalysisCache? cache, GCHandleAnalysisOptions options, IProgress<AnalyzerProgressReport>? progress, CancellationToken cancellationToken)
        {
            var scanCounter = new ObjectScanCounter("scanning GC handles", progress, reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

            // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): a
            // correctness fix, not an enhancement. Without a tree provider, totalPinnedRetainedBytes/
            // totalAsyncPinnedRetainedBytes only sum ResolveSize's *shallow* size per handle target —
            // "retained" was never true. When the exact tree is available, use it instead.
            IDominatorTreeProvider? handleTreeProvider = cache?.TryGetDominatorTreeProvider();
            int pinnedExactCount = 0, pinnedFallbackCount = 0;
            int asyncPinnedExactCount = 0, asyncPinnedFallbackCount = 0;

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var pinnedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            // P2-2: RefCounted handles back COM interop (RCW) lifetime; concentration by target
            // type surfaces COM object leaks that would otherwise be invisible in the handle count.
            var refCountedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            // P2-4: individual pinned-handle addresses for debugger follow-up. Bounded by pinned
            // handle count (already the scope of the existing per-handle byte resolution above),
            // not heap object count, so collecting the full set before ranking is cheap.
            var pinnedHandleAddresses = new List<PinnedHandleAddressEntry>();
            var pinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var asyncPinnedBytesByType = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var allTargetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var nullTargetHandlesByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            ulong totalPinnedRetainedBytes = 0;
            ulong totalAsyncPinnedRetainedBytes = 0;
            // P2-1: SOH targets keep the GC from compacting around them; LOH/POH/Frozen targets
            // don't (LOH is never compacted, POH objects are already pinned by construction), so
            // only the SOH count signals an actionable compaction barrier.
            int pinnedSohObjectCount = 0;
            int pinnedNonSohObjectCount = 0;
            int asyncPinnedSohObjectCount = 0;
            int asyncPinnedNonSohObjectCount = 0;
            // P3-2: WeakShort clears when the target becomes unreachable, even mid-finalization;
            // WeakLong clears only after finalization completes. A WeakLong population
            // concentrated in Gen2/LOH can indicate a finalization backlog (targets lingering,
            // weakly-referenced, waiting for their finalizer to run).
            int weakShortGen0Count = 0, weakShortGen1Count = 0, weakShortGen2Count = 0, weakShortLohCount = 0;
            int weakLongGen0Count = 0, weakLongGen1Count = 0, weakLongGen2Count = 0, weakLongLohCount = 0;
            // OPT-#9: Cache method-table -> type-name to avoid one heap.GetObject call per handle
            // for handles whose target type has already been resolved. Collapses N handles of
            // the same type into a single lookup. Also reused for dependent-handle target resolution.
            var methodTableNameCache = new Dictionary<ulong, string>(capacity: 128);

            int totalHandles = 0;
            int strongLikeHandles = 0;
            int weakLikeHandles = 0;
            int unknownTargetCount = 0;

            int dependentHandleCount = 0;
            int dependentResolvedEdgeCount = 0;
            int dependentUnresolvedTargetCount = 0;
            var dependentSourceTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependentTargetTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dependentSourceTargetPairCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            void ProcessHandle(ulong targetAddress, ulong methodTable, byte kindByte, ulong dependentTarget = 0)
            {
                scanCounter.Tick();
                totalHandles++;

                string kind = ((ClrHandleKind)kindByte).ToString();
                Increment(byKind, kind);

                if (IsWeakLike(kind))
                    weakLikeHandles++;
                else
                    strongLikeHandles++;

                // P3-3: dependentHandleCount counts every Dependent-kind handle unconditionally
                // (matching the prior live-enumeration pass, which incremented it before any
                // validity check) — same heap-is-null limitation the old pass had.
                bool isDependent = kind == "Dependent";
                if (isDependent && heap is not null)
                    dependentHandleCount++;

                // P1-3: Track null-target handles per kind
                if (targetAddress == 0)
                {
                    Increment(nullTargetHandlesByKind, kind);
                    if (isDependent && heap is not null)
                        dependentUnresolvedTargetCount++;
                    return;
                }

                string? typeName = ResolveTypeNameFromRecord(heap, targetAddress, methodTable, methodTableNameCache);
                if (typeName == null)
                {
                    unknownTargetCount++;
                    if (isDependent && heap is not null)
                        dependentUnresolvedTargetCount++;
                    return;
                }

                Increment(allTargetTypes, typeName);

                // P1-2: Separate AsyncPinned vs Pinned byte accounting
                if (kind == "AsyncPinned")
                {
                    // P2-1: SOH vs LOH/POH/Frozen classification for the pinned target
                    if (TryIsSoh(heap, targetAddress, out bool isSohAsync))
                    {
                        if (isSohAsync)
                            asyncPinnedSohObjectCount++;
                        else
                            asyncPinnedNonSohObjectCount++;
                    }

                    // §9: exact retained bytes when the dominator tree is available — this is what
                    // "would become collectible if this pin were released" actually means. Falls
                    // back to ResolveSize's shallow size (the target's own bytes, not what it
                    // transitively holds) when the tree can't answer for this address.
                    ulong resolvedSize;
                    if (handleTreeProvider is not null && handleTreeProvider.TryGetRetainedBytes(targetAddress, out ulong exactAsyncPinnedBytes))
                    {
                        resolvedSize = exactAsyncPinnedBytes;
                        asyncPinnedExactCount++;
                    }
                    else
                    {
                        resolvedSize = ResolveSize(heap, cache, targetAddress);
                        asyncPinnedFallbackCount++;
                    }

                    if (resolvedSize > 0)
                    {
                        totalAsyncPinnedRetainedBytes += resolvedSize;
                        if (asyncPinnedBytesByType.TryGetValue(typeName, out ulong existingBytes))
                            asyncPinnedBytesByType[typeName] = existingBytes + resolvedSize;
                        else
                            asyncPinnedBytesByType[typeName] = resolvedSize;

                        // P2-4: individual address entry for the top-N table
                        pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(targetAddress, typeName, resolvedSize, kind));
                    }
                }
                else if (kind == "Pinned")
                {
                    Increment(pinnedTypes, typeName);

                    // P2-1: SOH vs LOH/POH/Frozen classification for the pinned target
                    if (TryIsSoh(heap, targetAddress, out bool isSohPinned))
                    {
                        if (isSohPinned)
                            pinnedSohObjectCount++;
                        else
                            pinnedNonSohObjectCount++;
                    }

                    // §9: same exact-retained-bytes preference as the AsyncPinned branch above.
                    ulong resolvedSize;
                    if (handleTreeProvider is not null && handleTreeProvider.TryGetRetainedBytes(targetAddress, out ulong exactPinnedBytes))
                    {
                        resolvedSize = exactPinnedBytes;
                        pinnedExactCount++;
                    }
                    else
                    {
                        resolvedSize = ResolveSize(heap, cache, targetAddress);
                        pinnedFallbackCount++;
                    }

                    if (resolvedSize > 0)
                    {
                        totalPinnedRetainedBytes += resolvedSize;
                        if (pinnedBytesByType.TryGetValue(typeName, out ulong existingBytes))
                            pinnedBytesByType[typeName] = existingBytes + resolvedSize;
                        else
                            pinnedBytesByType[typeName] = resolvedSize;

                        // P2-4: individual address entry for the top-N table
                        pinnedHandleAddresses.Add(new PinnedHandleAddressEntry(targetAddress, typeName, resolvedSize, kind));
                    }
                }
                else if (kind == "RefCounted")
                {
                    // P2-2: COM interop (RCW) target type concentration
                    Increment(refCountedTypes, typeName);
                }
                else if (kind == "Dependent")
                {
                    // P3-3: dependent-handle topology, resolved inline from the snapshot-carried
                    // DependentTarget instead of a second live runtime.EnumerateHandles() pass.
                    // Source-type resolution still requires the live heap — matches the prior
                    // live-only pass, which likewise tracked nothing when heap was null.
                    if (heap is not null)
                    {
                        if (!TryResolveTypeNameStrict(heap, cache, targetAddress, methodTableNameCache, out string sourceType))
                        {
                            dependentUnresolvedTargetCount++;
                        }
                        else
                        {
                            Increment(dependentSourceTypeCounts, sourceType);

                            if (dependentTarget == 0
                                || !TryResolveTypeNameStrict(heap, cache, dependentTarget, methodTableNameCache, out string dependentTargetType))
                            {
                                dependentUnresolvedTargetCount++;
                            }
                            else
                            {
                                dependentResolvedEdgeCount++;
                                Increment(dependentTargetTypeCounts, dependentTargetType);
                                Increment(dependentSourceTargetPairCounts, $"{sourceType} -> {dependentTargetType}");
                            }
                        }
                    }
                }
                else if (kind == "WeakShort" || kind == "WeakLong")
                {
                    // P3-2: generation breakdown for weak handle targets
                    int generation = heap is null ? -1 : SegmentKindMapper.ResolveGeneration(heap, targetAddress);
                    if (generation < 0)
                    {
                        // Unresolvable segment — no bucket to attribute to.
                    }
                    else if (kind == "WeakShort")
                    {
                        if (generation == 0) weakShortGen0Count++;
                        else if (generation == 1) weakShortGen1Count++;
                        else if (generation == 2) weakShortGen2Count++;
                        else weakShortLohCount++;
                    }
                    else
                    {
                        if (generation == 0) weakLongGen0Count++;
                        else if (generation == 1) weakLongGen1Count++;
                        else if (generation == 2) weakLongGen2Count++;
                        else weakLongLohCount++;
                    }
                }
            }

            // P0-2/P3-3: Prefer the shared handle snapshot (disk or in-memory) built during
            // indexing over a second runtime.EnumerateHandles() call. The snapshot now carries
            // each Dependent handle's secondary target address (HandleRecord.DependentTarget,
            // P3-3), so dependent-handle topology is resolved inline in ProcessHandle above —
            // no separate live-enumeration pass is needed for it anymore.
            HeapIndexBuildResult? heapIndex = null;
            if (cache is HeapAnalysisCache heapCache)
                heapCache.TryGetHeapIndex(out heapIndex);

            if (heapIndex is not null && heapIndex.InMemoryHandleSnapshot is { Length: > 0 } inMemHandles)
            {
                foreach (var rec in inMemHandles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProcessHandle(rec.Addr, rec.Mt, rec.Kind, rec.DependentTarget);
                }
            }
            else
            {
                IHandleSnapshotReader? reader = null;
                if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk && heapIndex.IndexPath?.Length > 0)
                    reader = HandleSnapshotProvider.CreateFromDiskIfExists(heapIndex.IndexPath);
                if (reader is null && heap is not null)
                    reader = HandleSnapshotProvider.CreateMemoryReader(runtime, heap, int.MaxValue);

                if (reader is not null)
                {
                    using (reader)
                    {
                        foreach (HandleRecord rec in reader.EnumerateRecords(cancellationToken))
                            ProcessHandle(rec.Address, rec.MethodTable, rec.Kind, rec.DependentTarget);
                    }
                }
                else
                {
                    // No heap available to resolve method tables — fall back to raw enumeration.
                    // Dependent-target resolution doesn't need the heap (it's reflection over the
                    // live ClrHandle), so it's still available on this path.
                    foreach (ClrHandle handle in runtime.EnumerateHandles())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ulong dependentTarget = 0;
                        if (handle.HandleKind == ClrHandleKind.Dependent)
                            DependentHandleTargetResolver.TryGetDependentTargetAddress(handle, out dependentTarget);
                        ProcessHandle(GetTargetAddress(handle), 0, (byte)handle.HandleKind, dependentTarget);
                    }
                }
            }

            scanCounter.Complete();

            int pinnedHandleTargets = pinnedTypes.Values.Sum();
            int refCountedHandleCount = refCountedTypes.Values.Sum();
            double dependentUnresolvedPercent = dependentHandleCount == 0 ? 0
                : dependentUnresolvedTargetCount * 100.0 / dependentHandleCount;

            // P2-4: rank the exact set of collected pinned-handle addresses by bytes, keep top N for display.
            pinnedHandleAddresses.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
            int topPinnedAddressCount = Math.Min(options.TopPinnedHandleAddressesToShow, pinnedHandleAddresses.Count);
            var topPinnedHandleAddresses = pinnedHandleAddresses.GetRange(0, topPinnedAddressCount);

            static List<NameCountEntry> ToRankedEntries(Dictionary<string, int> source)
            {
                var list = new List<NameCountEntry>(source.Count);
                foreach (var kvp in source)
                    list.Add(new NameCountEntry(kvp.Key, kvp.Value));
                list.Sort(static (a, b) => b.Count.CompareTo(a.Count));
                return list;
            }
            static List<NameBytesEntry> ToRankedByteEntries(Dictionary<string, ulong> source)
            {
                var list = new List<NameBytesEntry>(source.Count);
                foreach (var kvp in source)
                    list.Add(new NameBytesEntry(kvp.Key, kvp.Value));
                list.Sort(static (a, b) => b.Bytes.CompareTo(a.Bytes));
                return list;
            }

            return new GCHandleDomainResult(
                totalHandles,
                strongLikeHandles,
                weakLikeHandles,
                pinnedHandleTargets,
                ToRankedEntries(byKind),
                ToRankedEntries(allTargetTypes),
                ToRankedEntries(pinnedTypes),
                totalPinnedRetainedBytes,
                ToRankedByteEntries(pinnedBytesByType),
                totalAsyncPinnedRetainedBytes,
                ToRankedByteEntries(asyncPinnedBytesByType),
                ToRankedEntries(nullTargetHandlesByKind),
                unknownTargetCount,
                dependentHandleCount,
                dependentResolvedEdgeCount,
                dependentUnresolvedTargetCount,
                dependentUnresolvedPercent,
                ToRankedEntries(dependentSourceTypeCounts),
                ToRankedEntries(dependentTargetTypeCounts),
                ToRankedEntries(dependentSourceTargetPairCounts),
                PinnedRetainedBytesIsExact: pinnedExactCount > 0 && pinnedFallbackCount == 0,
                AsyncPinnedRetainedBytesIsExact: asyncPinnedExactCount > 0 && asyncPinnedFallbackCount == 0,
                PinnedSohObjectCount: pinnedSohObjectCount,
                PinnedNonSohObjectCount: pinnedNonSohObjectCount,
                AsyncPinnedSohObjectCount: asyncPinnedSohObjectCount,
                AsyncPinnedNonSohObjectCount: asyncPinnedNonSohObjectCount,
                RefCountedHandleCount: refCountedHandleCount,
                TopRefCountedTargetTypes: ToRankedEntries(refCountedTypes),
                TopPinnedHandleAddresses: topPinnedHandleAddresses,
                WeakShortGen0Count: weakShortGen0Count,
                WeakShortGen1Count: weakShortGen1Count,
                WeakShortGen2Count: weakShortGen2Count,
                WeakShortLohCount: weakShortLohCount,
                WeakLongGen0Count: weakLongGen0Count,
                WeakLongGen1Count: weakLongGen1Count,
                WeakLongGen2Count: weakLongGen2Count,
                WeakLongLohCount: weakLongLohCount,
                TotalHandlesWarningThreshold: options.TotalHandlesWarningThreshold,
                PinnedHandleTargetsWarningThreshold: options.PinnedHandleTargetsWarningThreshold,
                PinnedRetainedBytesWarningThreshold: options.PinnedRetainedBytesWarningThreshold,
                PinnedSohObjectCountWarningThreshold: options.PinnedSohObjectCountWarningThreshold,
                RefCountedHandleCountWarningThreshold: options.RefCountedHandleCountWarningThreshold,
                WeakLongGen2FractionWarningThreshold: options.WeakLongGen2FractionWarningThreshold,
                WeakLongGen2MinimumCountThreshold: options.WeakLongGen2MinimumCountThreshold,
                DependentUnresolvedPercentWarningThreshold: options.DependentUnresolvedPercentWarningThreshold);
        }

        public void Dispose() { }

        private static bool IsWeakLike(string kind)
        {
            return kind.Contains("Weak", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves whether <paramref name="address"/> lives on the small object heap via a single
        /// <c>ClrSegment</c> lookup (bounded by pinned-handle count, not heap size — same cost class as
        /// the existing per-handle <c>ClrObject.Size</c> read). Returns false (with <paramref name="isSoh"/>
        /// unset) when <paramref name="heap"/> is null or the address doesn't resolve to a segment.
        /// </summary>
        private static bool TryIsSoh(ClrHeap? heap, ulong address, out bool isSoh)
        {
            isSoh = false;
            if (heap is null)
                return false;

            ClrSegment? segment = heap.GetSegmentByAddress(address);
            if (segment is null)
                return false;

            isSoh = SegmentKindMapper.Map(segment) == HeapSegmentKind.SmallObjectHeap;
            return true;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int value))
                counts[key] = value + 1;
            else
                counts[key] = 1;
        }

        private static ulong GetTargetAddress(ClrHandle handle)
        {
            object boxedTarget = handle.Object;

            if (boxedTarget is ClrObject clrObject)
            {
                return clrObject.IsValid ? clrObject.Address : 0;
            }

            if (boxedTarget is ulong address)
            {
                return address;
            }

            return 0;
        }

        private static bool TryResolveTypeNameStrict(ClrHeap heap, IHeapAnalysisCache? cache, ulong address, Dictionary<ulong, string> methodTableNameCache, out string typeName)
        {
            typeName = StringConstants.UnknownType;

            if (address == 0)
                return false;

            // OPT (docs/cache/cache-architecture.md Phase 6): address-only caller — resolve
            // via the disk-backed address index instead of heap.GetObject when available.
            ulong methodTable;
            if (cache is not null)
            {
                if (!cache.TryGetObjectMetadata(heap, address, out methodTable, out _) || methodTable == 0)
                    return false;
            }
            else
            {
                ClrObject obj = heap.GetObject(address);
                if (!obj.IsValid || obj.Type == null)
                    return false;
                methodTable = obj.Type.MethodTable;
            }

            if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cachedName))
            {
                typeName = cachedName;
                return true;
            }

            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            typeName = type?.Name ?? StringConstants.UnknownType;
            if (methodTable != 0)
                methodTableNameCache[methodTable] = typeName;

            return true;
        }

        /// <summary>
        /// Resolves an object's size via the disk-backed address index when
        /// <paramref name="cache"/> is available, falling back to a live
        /// <c>heap.GetObject</c> resolution otherwise. Returns 0 for a null <paramref name="heap"/>
        /// or an unresolvable <paramref name="address"/> — same contract the inline
        /// <c>heap.GetObject(address).Size</c> checks this replaces already had.
        /// </summary>
        private static ulong ResolveSize(ClrHeap? heap, IHeapAnalysisCache? cache, ulong address)
        {
            if (heap is null)
                return 0;

            if (cache is not null)
                return cache.TryGetObjectMetadata(heap, address, out _, out ulong size) ? size : 0;

            ClrObject obj = heap.GetObject(address);
            return obj.IsValid ? obj.Size : 0;
        }

        private static string? ResolveTypeNameFromRecord(ClrHeap? heap, ulong targetAddress, ulong methodTable, Dictionary<ulong, string> methodTableNameCache)
        {
            if (targetAddress == 0)
                return null;

            if (methodTable != 0 && methodTableNameCache.TryGetValue(methodTable, out string? cached))
                return cached;

            if (heap == null)
                return $"Object@0x{targetAddress:X}";

            ClrType? type = methodTable != 0 ? heap.GetTypeByMethodTable(methodTable) : null;
            if (type == null)
                return $"Object@0x{targetAddress:X}";

            string name = type.Name ?? StringConstants.UnknownType;
            if (methodTable != 0)
                methodTableNameCache[methodTable] = name;

            return name;
        }
    }
}
