using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §24.1–§24.3 (weak GC handle population, WeakReference&lt;T&gt;
    /// object analysis, and ConditionalWeakTable dead-key analysis).
    ///
    /// Reads <c>HandleSnapshot.bin</c> when the disk index is available to avoid a second
    /// <c>runtime.EnumerateHandles()</c> call. Falls back to live enumeration in memory mode.
    /// Bounded by configured options to stay predictable on large dumps.
    /// </summary>
    public sealed class WeakReferenceAnalyzer : IAnalyzer, IRequiresReachableGraphIndex
    {
        // ── Constants ─────────────────────────────────────────────────────────
        // ClrHandleKind enum values (Microsoft.Diagnostics.Runtime):
        //   WeakShort = 0, WeakLong = 1, Strong = 2, Pinned = 3,
        //   RefCounted = 5, Dependent = 6, AsyncPinned = 7, SizedRef = 8, WeakWinRT = 9
        private const byte KindWeakShort = 0;
        private const byte KindWeakLong = 1;
        private const byte KindDependent = 6;
        private const byte KindWeakWinRT = 9;

        private const string WeakRefGenericName = "System.WeakReference`1";
        private const string WeakRefNonGenericName = "System.WeakReference";


        public string Name => "Weak Reference Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WeakReferenceAnalysisOptions options = context.AnalysisOptions.WeakReferenceAnalysis;
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, options, context.Progress, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrRuntime runtime,
            ClrHeap heap,
            IHeapAnalysisCache cache,
            WeakReferenceAnalysisOptions options,
            IProgress<AnalyzerProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            // ── Resolve index info ────────────────────────────────────────────
            IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
            HeapIndexBuildResult? heapIndex = null;

            if (cache is HeapAnalysisCache heapCache && heapCache.TryGetHeapIndex(out heapIndex))
                typeAggregates = heapIndex.TypeAggregates;

            // ── Phases A & C: Weak handle liveness + Dependent handle dead-key count ──
            // Merged into single pass over handle snapshot to halve disk I/O (P1-2)
            progress?.Report(new(0, "scanning weak and dependent handles"));

            int totalWeakHandles = 0;
            int aliveWeakTargets = 0;
            int deadWeakTargets = 0;

            // §9 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): a genuine
            // enhancement, not a fix — WeakReferenceObjectBytes already honestly reports the
            // wrapper objects' own shallow size. This adds "what does each alive weak target
            // transitively retain?" alongside it, when the exact tree is available.
            IDominatorTreeProvider? weakTreeProvider = cache.TryGetDominatorTreeProvider();
            ulong aliveWeakTargetsRetainedBytes = 0;

            var targetTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var weakHandleKinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var aliveByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var deadByKind = new Dictionary<string, int>(StringComparer.Ordinal);

            int dependentHandleDeadKeyCount = 0;
            var dependentDeadValueTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            int dependentDeadValueUnresolvedCount = 0;

            var aliveGenerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int aliveGenerationUnresolvedCount = 0;

            // §24.1 P3-2: "held only via weak reference" detection. Uses the shared Stage A
            // reachability walk (IReachableAddressProvider) rather than a raw
            // IBackwardReferenceProvider.TryGetParents lookup as the audit's own note proposed:
            // the reverse-edge index only records object-to-object edges, so an object rooted
            // directly (e.g. a static field, with no other object pointing at it) would show zero
            // recorded parents there too — a false positive. Stage A's walk starts from
            // heap.EnumerateRoots() (which, like every ClrMD root enumeration, never includes weak
            // or dependent handles) and follows only strong references, so IsReachable(addr) == false
            // for a still-alive object means nothing but the weak handle itself currently points at
            // it — exactly the signal dotMemory calls "held only via weak reference".
            IReachableAddressProvider? reachableProvider = cache.TryGetReachableAddressProvider();
            var heldOnlyViaWeakReferenceTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            int heldOnlyViaWeakReferenceCount = 0;

            void TrackHeldOnlyViaWeakReference(ulong addr, string typeName)
            {
                if (reachableProvider is not null && !reachableProvider.IsReachable(addr))
                {
                    heldOnlyViaWeakReferenceCount++;
                    IncrementDict(heldOnlyViaWeakReferenceTypeHits, typeName);
                }
            }

            // §24.1 P3-3: GC generation distribution of alive weak targets, via the object's
            // current segment (SegmentKindMapper.ResolveGeneration — same convention already
            // used by GCHandleAnalyzer's P3-2 weak-handle-target generation breakdown). Scoped
            // to alive targets only: a dead weak handle's address is either already cleared
            // (0) by the GC when the target was collected, or — if still non-zero — points at
            // memory that may since have been reused by an unrelated object, so resolving "its"
            // generation would attribute a misleading value rather than the collected object's
            // actual former generation.
            void TrackAliveWeakTargetGeneration(ulong addr)
            {
                int generation = SegmentKindMapper.ResolveGeneration(heap, addr);
                string? bucket = generation switch
                {
                    0 => "Gen0",
                    1 => "Gen1",
                    2 => "Gen2",
                    >= 3 => "LOH",
                    _ => null
                };
                if (bucket is null)
                    aliveGenerationUnresolvedCount++;
                else
                    IncrementDict(aliveGenerationCounts, bucket);
            }

            // §24.3 P3-1: for a dead-key dependent handle, resolve the secondary (value) object's
            // type — reveals what data ConditionalWeakTable is orphaning, not just how many keys
            // died. Scoped to dead keys only; distinct from GCHandleAnalyzer's P3-3 breakdown,
            // which covers source->target type pairs for all dependent handles (alive and dead).
            void TrackDependentDeadKeyValueType(ulong dependentTarget)
            {
                if (dependentTarget != 0 && cache.TryGetObjectMetadata(heap, dependentTarget, out ulong valueMt, out _))
                {
                    string valueTypeName = valueMt != 0 ? (heap.GetTypeByMethodTable(valueMt)?.Name ?? "Unknown") : "Unknown";
                    IncrementDict(dependentDeadValueTypeHits, valueTypeName);
                }
                else
                {
                    dependentDeadValueUnresolvedCount++;
                }
            }

            // Optional exports
            IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? rawExports = null;
            string? tmpNdjsonPath = null;
            System.IO.FileStream? tmpFs = null;
            System.IO.Compression.GZipStream? tmpGz = null;
            var sampleRecords = new List<object>();
            var jsOpts = new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
            void WriteExportRecord(ulong a, ulong mt, byte k)
            {
                try
                {
                    if (tmpGz is null) return;
                    var obj = new { address = a, methodTable = mt, kind = k };
                    System.Text.Json.JsonSerializer.Serialize(tmpGz, obj, jsOpts);
                    tmpGz.WriteByte((byte)'\n');
                    if (sampleRecords.Count < 100) sampleRecords.Add(obj);
                }
                catch { }
            }

            // Prepare exporter if requested
            if (options.ProduceRawExports)
            {
                try
                {
                    tmpNdjsonPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dumpdetective-weakrefs-{Guid.NewGuid():N}.ndjson.gz");
                    tmpFs = System.IO.File.Create(tmpNdjsonPath);
                    tmpGz = new System.IO.Compression.GZipStream(tmpFs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: false);
                }
                catch { tmpGz = null; tmpFs = null; tmpNdjsonPath = null; }
            }

            // Try to reuse any pre-enumerated in-memory handle snapshot (memory-index mode)
            if (heapIndex is not null && heapIndex.InMemoryHandleSnapshot is { Length: > 0 } inMem)
            {
                foreach (var rec in inMem)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Phase A: Weak kind branch
                    if (rec.Kind == KindWeakShort || rec.Kind == KindWeakLong || rec.Kind == KindWeakWinRT)
                    {
                        totalWeakHandles++;
                        string kindName = KindToName(rec.Kind);
                        IncrementDict(weakHandleKinds, kindName);

                        ulong addr = rec.Addr;
                        if (addr == 0)
                        {
                            deadWeakTargets++;
                            IncrementDict(deadByKind, kindName);
                        }
                        else
                        {
                            // OPT (docs/cache/cache-architecture.md Phase 6): resolve via
                            // the disk-backed address index instead of heap.GetObject.
                            if (cache.TryGetObjectMetadata(heap, addr, out ulong mt, out _))
                            {
                                aliveWeakTargets++;
                                IncrementDict(aliveByKind, kindName);
                                string typeName = mt != 0 ? (heap.GetTypeByMethodTable(mt)?.Name ?? "Unknown") : "Unknown";
                                IncrementDict(targetTypeHits, typeName);
                                TrackAliveWeakTargetGeneration(addr);
                                TrackHeldOnlyViaWeakReference(addr, typeName);
                                if (weakTreeProvider is not null && weakTreeProvider.TryGetRetainedBytes(addr, out ulong retained))
                                    aliveWeakTargetsRetainedBytes += retained;
                            }
                            else
                            {
                                deadWeakTargets++;
                                IncrementDict(deadByKind, kindName);
                            }
                        }
                        if (options.ProduceRawExports)
                            WriteExportRecord(rec.Addr, rec.Mt, rec.Kind);
                    }

                    // Phase C: Dependent kind branch
                    else if (rec.Kind == KindDependent)
                    {
                        ulong addr = rec.Addr;
                        if (addr == 0)
                        {
                            dependentHandleDeadKeyCount++;
                            TrackDependentDeadKeyValueType(rec.DependentTarget);
                        }
                        else
                        {
                            // OPT (docs/cache/cache-architecture.md Phase 6): resolve via
                            // the disk-backed address index instead of heap.GetObject.
                            if (!cache.TryGetObjectMetadata(heap, addr, out _, out _))
                            {
                                dependentHandleDeadKeyCount++;
                                TrackDependentDeadKeyValueType(rec.DependentTarget);
                            }
                        }
                        if (options.ProduceRawExports)
                            WriteExportRecord(rec.Addr, rec.Mt, rec.Kind);
                    }
                }
            }
            else
            {
                // Otherwise try disk-backed snapshot, or enumerate live handles via a memory reader.
                IHandleSnapshotReader? reader = null;
                if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk && heapIndex.IndexPath?.Length > 0)
                {
                    reader = HandleSnapshotProvider.CreateFromDiskIfExists(heapIndex.IndexPath);
                }
                reader ??= HandleSnapshotProvider.CreateMemoryReader(runtime, heap, int.MaxValue);

                var scanCounter = new ObjectScanCounter("scanning weak and dependent handles", progress,
                    reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

                if (reader is not null)
                {
                    using (reader)
                    {
                        foreach (var rec in reader.EnumerateRecords(cancellationToken))
                        {
                            scanCounter.Tick();
                            cancellationToken.ThrowIfCancellationRequested();

                            // Phase A: Weak kind branch
                            if (rec.Kind == KindWeakShort || rec.Kind == KindWeakLong || rec.Kind == KindWeakWinRT)
                            {
                                totalWeakHandles++;
                                string kindName = KindToName(rec.Kind);
                                IncrementDict(weakHandleKinds, kindName);

                                ulong addr = rec.Address;
                                if (addr == 0)
                                {
                                    deadWeakTargets++;
                                    IncrementDict(deadByKind, kindName);
                                }
                                else
                                {
                                    // OPT (docs/cache/cache-architecture.md Phase 6): resolve
                                    // via the disk-backed address index instead of heap.GetObject.
                                    if (cache.TryGetObjectMetadata(heap, addr, out ulong mt, out _))
                                    {
                                        aliveWeakTargets++;
                                        IncrementDict(aliveByKind, kindName);
                                        string typeName = mt != 0 ? (heap.GetTypeByMethodTable(mt)?.Name ?? "Unknown") : "Unknown";
                                        IncrementDict(targetTypeHits, typeName);
                                        TrackAliveWeakTargetGeneration(addr);
                                        TrackHeldOnlyViaWeakReference(addr, typeName);
                                        if (weakTreeProvider is not null && weakTreeProvider.TryGetRetainedBytes(addr, out ulong retained))
                                            aliveWeakTargetsRetainedBytes += retained;
                                    }
                                    else
                                    {
                                        deadWeakTargets++;
                                        IncrementDict(deadByKind, kindName);
                                    }
                                }
                                if (options.ProduceRawExports)
                                    WriteExportRecord(rec.Address, rec.MethodTable, rec.Kind);
                            }

                            // Phase C: Dependent kind branch
                            else if (rec.Kind == KindDependent)
                            {
                                ulong addr = rec.Address;
                                if (addr == 0)
                                {
                                    dependentHandleDeadKeyCount++;
                                    TrackDependentDeadKeyValueType(rec.DependentTarget);
                                }
                                else
                                {
                                    if (!rec.IsAlive)
                                    {
                                        dependentHandleDeadKeyCount++;
                                        TrackDependentDeadKeyValueType(rec.DependentTarget);
                                    }
                                }
                                if (options.ProduceRawExports)
                                    WriteExportRecord(rec.Address, rec.MethodTable, rec.Kind);
                            }
                        }
                        scanCounter.Complete();
                    }
                }
            }

            // Dispose export stream after both phases complete
            try { tmpGz?.Dispose(); tmpGz = null; tmpFs = null; }
            catch { }

            // ── Phase B: WeakReference<T> object analysis ─────────────────────
            int weakRefObjCount = 0;
            ulong weakRefObjBytes = 0;
            int staleWrapperCount = 0;
            var staleHolderTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            bool phaseBFallbackUsed = false;
            bool phaseBSkipped = false;
            bool staleWrapperCountIsExact = false;

            if (typeAggregates is not null)
            {
                // Index path: use TypeAggregates
                progress?.Report(new(0, "scanning WeakReference objects"));

                var weakRefTypesByMt = new Dictionary<ulong, TypeAggregateIndexEntry>();

                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
                {
                    ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                    if (clrType is null) continue;

                    string? name = clrType.Name;
                    if (name is null) continue;

                    bool isGenericWR = name.StartsWith(WeakRefGenericName, StringComparison.Ordinal);
                    bool isNonGenericWR = string.Equals(name, WeakRefNonGenericName, StringComparison.Ordinal);

                    if (isGenericWR || isNonGenericWR)
                        weakRefTypesByMt[kv.Key] = kv.Value;
                }

                foreach (TypeAggregateIndexEntry entry in weakRefTypesByMt.Values)
                {
                    weakRefObjCount += (int)Math.Min(entry.Count, int.MaxValue);
                    weakRefObjBytes += entry.TotalSize;
                }

                // §24.2 P3-4: exact stale-wrapper count via a single streaming pass over the
                // disk-backed object index, filtered to the small set of WeakReference<T>-shaped
                // MethodTables found above — same convention already used by
                // AsyncStateMachineAnalyzer's suspend-state histogram, TimerLeakAnalyzer, and
                // EventLeak/PublisherRegistry. Replaces the former one-sample-per-type
                // extrapolation (which could be up to 100% wrong per type group — see
                // weak-reference-analyzer-audit.md Bug 4). Per-record cost of the streaming pass
                // is cheap (an Address/MT/Size tuple); heap.GetObject and the field read only
                // happen for entries whose MT is one of the handful of WeakReference-shaped types.
                if (weakRefTypesByMt.Count > 0 && cache.EnumerateIndexedEntriesAsTuples().Any())
                {
                    var mHandleFieldByMt = new Dictionary<ulong, ClrInstanceField?>(weakRefTypesByMt.Count);
                    var wrScanCounter = new ObjectScanCounter("scanning WeakReference instances",
                        progress, reportEveryObjects: 50_000, reportEveryElapsed: TimeSpan.FromSeconds(2));

                    foreach ((ulong address, ulong mt, ulong _) in cache.EnumerateIndexedEntriesAsTuples())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        wrScanCounter.Tick();

                        if (!weakRefTypesByMt.ContainsKey(mt))
                            continue;

                        if (!mHandleFieldByMt.TryGetValue(mt, out ClrInstanceField? mHandleField))
                        {
                            mHandleField = heap.GetTypeByMethodTable(mt)?.GetFieldByName("m_handle");
                            mHandleFieldByMt[mt] = mHandleField;
                        }
                        if (mHandleField is null) continue;

                        ClrObject obj = heap.GetObject(address);
                        if (!obj.IsValid || obj.Type is null) continue;

                        nint handleValue;
                        try { handleValue = mHandleField.Read<nint>(address, interior: false); }
                        catch { continue; }

                        if (handleValue == 0)
                        {
                            staleWrapperCount++;
                            IncrementDict(staleHolderTypeHits, obj.Type.Name ?? "Unknown");
                        }
                    }

                    wrScanCounter.Complete();
                    staleWrapperCountIsExact = true;
                }
                else if (weakRefTypesByMt.Count > 0)
                {
                    // Degraded fallback: TypeAggregates present but the disk object index isn't
                    // (e.g. a hand-built test fixture, or a partial/aborted index write) — should
                    // not occur in production, since both come from the same
                    // DiskBackedObjectIndexWriter.Build call. Extrapolate from one sample address
                    // per type, same approximation the exact scan above replaces.
                    // staleWrapperCountIsExact stays false.
                    foreach (TypeAggregateIndexEntry entry in weakRefTypesByMt.Values)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (entry.SampleAddress == 0) continue;

                        ClrObject sample = heap.GetObject(entry.SampleAddress);
                        if (!sample.IsValid || sample.Type is null) continue;

                        ClrInstanceField? mHandleField = sample.Type.GetFieldByName("m_handle");
                        if (mHandleField is null) continue;

                        nint handleValue = mHandleField.Read<nint>(entry.SampleAddress, interior: false);
                        if (handleValue == 0)
                        {
                            staleWrapperCount += (int)Math.Min(entry.Count, int.MaxValue);
                            IncrementDict(staleHolderTypeHits, sample.Type.Name ?? "Unknown");
                        }
                    }
                }
            }
            else
            {
                // Fallback path: scan heap when TypeAggregates unavailable (P1-3)
                progress?.Report(new(0, "scanning WeakReference objects (fallback mode)"));

                var scanCounter = new ObjectScanCounter("scanning WeakReference objects", progress,
                    reportEveryObjects: 10000, reportEveryElapsed: TimeSpan.FromSeconds(2));

                phaseBFallbackUsed = true;
                staleWrapperCountIsExact = true;

                foreach (var obj in heap.EnumerateObjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanCounter.Tick();

                    if (obj.Type is null) continue;

                    string? name = obj.Type.Name;
                    if (name is null) continue;

                    bool isGenericWR = name.StartsWith(WeakRefGenericName, StringComparison.Ordinal);
                    bool isNonGenericWR = string.Equals(name, WeakRefNonGenericName, StringComparison.Ordinal);

                    if (!isGenericWR && !isNonGenericWR) continue;

                    weakRefObjCount++;
                    weakRefObjBytes += obj.Size;

                    ClrInstanceField? mHandleField = obj.Type.GetFieldByName("m_handle");
                    if (mHandleField is null) continue;

                    try
                    {
                        nint handleValue = mHandleField.Read<nint>(obj.Address, interior: false);
                        if (handleValue == 0)
                        {
                            staleWrapperCount++;
                            string holderTypeName = obj.Type.Name ?? "Unknown";
                            IncrementDict(staleHolderTypeHits, holderTypeName);
                        }
                    }
                    catch
                    {
                    }
                }

                scanCounter.Complete();
            }

            // Attach artifacts if exports were requested and produced
            if (options.ProduceRawExports)
            {
                try
                {
                    var artifacts = new List<DumpDetective.Core.Models.ReportArtifact>();
                    try
                    {
                        var summary = new
                        {
                            totalWeakHandles,
                            aliveWeakTargets,
                            deadWeakTargets,
                            dependentHandleDeadKeyCount,
                            sampleRecords
                        };
                        string prettyJson = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        artifacts.Add(new DumpDetective.Core.Models.ReportArtifact("Weak Reference Analysis", "weakrefs.json", prettyJson, "application/json"));
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(tmpNdjsonPath) && System.IO.File.Exists(tmpNdjsonPath))
                    {
                        artifacts.Add(new DumpDetective.Core.Models.ReportArtifact("Weak Reference Analysis", "weakrefs.ndjson.gz", null, "application/gzip", tmpNdjsonPath));
                    }

                    if (artifacts.Count > 0) rawExports = artifacts;
                }
                catch { rawExports = null; }
            }

            // ── Build output ──────────────────────────────────────────────────
            double deadRatio = totalWeakHandles == 0
                ? 0.0
                : (double)deadWeakTargets / totalWeakHandles;

            var topTargetTypes = BuildSorted(targetTypeHits);
            var topStaleTypes = BuildSorted(staleHolderTypeHits);
            var kindLiveness = BuildKindLiveness(aliveByKind, deadByKind);
            var dependentDeadKeyValueTypes = BuildSorted(dependentDeadValueTypeHits);
            var aliveGenerationDistribution = BuildGenerationDistribution(aliveGenerationCounts);
            var heldOnlyViaWeakReferenceTopTypes = BuildSorted(heldOnlyViaWeakReferenceTypeHits);

            return new WeakReferenceDomainResult(
                TotalWeakHandles: totalWeakHandles,
                AliveWeakTargets: aliveWeakTargets,
                DeadWeakTargets: deadWeakTargets,
                DeadTargetRatio: deadRatio,
                WeakHandleKinds: BuildSorted(weakHandleKinds),
                WeakReferenceObjectCount: weakRefObjCount,
                WeakReferenceObjectBytes: weakRefObjBytes,
                StaleWrapperCount: staleWrapperCount,
                StaleWrapperCountIsExact: staleWrapperCountIsExact,
                TopWeakTargetTypes: topTargetTypes,
                TopStaleWrapperHolderTypes: topStaleTypes,
                DependentHandleDeadKeyCount: dependentHandleDeadKeyCount,
                PhaseBFallbackUsed: phaseBFallbackUsed,
                PhaseBSkipped: phaseBSkipped,
                Artifacts: rawExports,
                AliveWeakTargetsRetainedBytes: aliveWeakTargetsRetainedBytes,
                AliveWeakTargetsRetainedBytesIsExact: weakTreeProvider is not null,
                WeakHandleKindLiveness: kindLiveness,
                DependentDeadKeyValueTypes: dependentDeadKeyValueTypes,
                DependentDeadKeyValueTypesUnresolvedCount: dependentDeadValueUnresolvedCount,
                AliveWeakTargetGenerationDistribution: aliveGenerationDistribution,
                AliveWeakTargetGenerationUnresolvedCount: aliveGenerationUnresolvedCount,
                HeldOnlyViaWeakReferenceCount: heldOnlyViaWeakReferenceCount,
                HeldOnlyViaWeakReferenceTopTypes: heldOnlyViaWeakReferenceTopTypes,
                HeldOnlyViaWeakReferenceDetectionAvailable: reachableProvider is not null);
        }


        // ── Utility ───────────────────────────────────────────────────────────

        private static void IncrementDict(Dictionary<string, int> dict, string key)
        {
            if (dict.TryGetValue(key, out int v))
                dict[key] = v + 1;
            else
                dict[key] = 1;
        }

        private static string KindToName(byte kind)
        {
            return kind switch
            {
                KindWeakShort => "WeakShort",
                KindWeakLong => "WeakLong",
                KindDependent => "Dependent",
                KindWeakWinRT => "WeakWinRT",
                _ => $"Kind{kind}"
            };
        }

        // §24.1 P2-1: per-kind alive/dead breakdown, descending by total handle count. Only
        // 3 possible kinds (WeakShort/WeakLong/WeakWinRT) so no cap needed.
        private static List<HandleKindLivenessEntry> BuildKindLiveness(
            Dictionary<string, int> aliveByKind,
            Dictionary<string, int> deadByKind)
        {
            var kinds = new HashSet<string>(aliveByKind.Keys, StringComparer.Ordinal);
            kinds.UnionWith(deadByKind.Keys);

            var list = new List<HandleKindLivenessEntry>(kinds.Count);
            foreach (string kind in kinds)
            {
                aliveByKind.TryGetValue(kind, out int alive);
                deadByKind.TryGetValue(kind, out int dead);
                list.Add(new HandleKindLivenessEntry(kind, alive, dead));
            }
            list.Sort(static (a, b) => b.Total.CompareTo(a.Total));
            return list;
        }

        // §24.1 P3-3: fixed Gen0 -> Gen1 -> Gen2 -> LOH ordering (generation progression), not
        // sorted by count — reads more naturally than the descending-count convention used
        // elsewhere in this file for open-ended type breakdowns.
        private static readonly string[] GenerationBucketOrder = ["Gen0", "Gen1", "Gen2", "LOH"];

        private static List<NameCountEntry> BuildGenerationDistribution(Dictionary<string, int> source)
        {
            var list = new List<NameCountEntry>(GenerationBucketOrder.Length);
            foreach (string bucket in GenerationBucketOrder)
            {
                if (source.TryGetValue(bucket, out int count) && count > 0)
                    list.Add(new NameCountEntry(bucket, count));
            }
            return list;
        }

        // Complete population, descending by count — no Top-N cap (§11.2 D5); the render layer
        // slices for display. Dictionaries here are O(distinct types), cheap to sort in full.
        private static List<NameCountEntry> BuildSorted(Dictionary<string, int> source)
        {
            var list = new List<NameCountEntry>(source.Count);
            foreach (KeyValuePair<string, int> kv in source.OrderByDescending(static x => x.Value))
            {
                list.Add(new NameCountEntry(kv.Key, kv.Value));
            }
            return list;
        }

        public void Dispose() { }
    }
}
