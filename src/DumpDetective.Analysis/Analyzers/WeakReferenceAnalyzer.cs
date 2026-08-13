using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
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
    public sealed class WeakReferenceAnalyzer : IAnalyzer
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
            bool scanCapped = false;

            var targetTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var weakHandleKinds = new Dictionary<string, int>(StringComparer.Ordinal);

            int dependentHandleDeadKeyCount = 0;

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
                        IncrementDict(weakHandleKinds, KindToName(rec.Kind));
                        if (totalWeakHandles > options.HandleScanCap) { scanCapped = true; break; }

                        ulong addr = rec.Addr;
                        if (addr == 0)
                        {
                            deadWeakTargets++;
                        }
                        else
                        {
                            // OPT (docs/cache/19-ObjectAddressLookupIndex.md Phase 6): resolve via
                            // the disk-backed address index instead of heap.GetObject.
                            if (cache.TryGetObjectMetadata(heap, addr, out ulong mt, out _))
                            {
                                aliveWeakTargets++;
                                string typeName = mt != 0 ? (heap.GetTypeByMethodTable(mt)?.Name ?? "Unknown") : "Unknown";
                                IncrementDict(targetTypeHits, typeName);
                            }
                            else
                            {
                                deadWeakTargets++;
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
                        }
                        else
                        {
                            // OPT (docs/cache/19-ObjectAddressLookupIndex.md Phase 6): resolve via
                            // the disk-backed address index instead of heap.GetObject.
                            if (!cache.TryGetObjectMetadata(heap, addr, out _, out _)) dependentHandleDeadKeyCount++;
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
                reader ??= HandleSnapshotProvider.CreateMemoryReader(runtime, heap, options.HandleScanCap);

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
                                IncrementDict(weakHandleKinds, KindToName(rec.Kind));
                                if (totalWeakHandles > options.HandleScanCap) { scanCapped = true; break; }

                                ulong addr = rec.Address;
                                if (addr == 0)
                                {
                                    deadWeakTargets++;
                                }
                                else
                                {
                                    // OPT (docs/cache/19-ObjectAddressLookupIndex.md Phase 6): resolve
                                    // via the disk-backed address index instead of heap.GetObject.
                                    if (cache.TryGetObjectMetadata(heap, addr, out ulong mt, out _))
                                    {
                                        aliveWeakTargets++;
                                        string typeName = mt != 0 ? (heap.GetTypeByMethodTable(mt)?.Name ?? "Unknown") : "Unknown";
                                        IncrementDict(targetTypeHits, typeName);
                                    }
                                    else
                                    {
                                        deadWeakTargets++;
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
                                }
                                else
                                {
                                    if (!rec.IsAlive) dependentHandleDeadKeyCount++;
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

            if (typeAggregates is not null)
            {
                // Index path: use TypeAggregates
                progress?.Report(new(0, "scanning WeakReference objects"));

                var weakRefMtEntries = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>(4);

                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
                {
                    ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                    if (clrType is null) continue;

                    string? name = clrType.Name;
                    if (name is null) continue;

                    bool isGenericWR = name.StartsWith(WeakRefGenericName, StringComparison.Ordinal);
                    bool isNonGenericWR = string.Equals(name, WeakRefNonGenericName, StringComparison.Ordinal);

                    if (isGenericWR || isNonGenericWR)
                        weakRefMtEntries.Add((kv.Key, kv.Value));
                }

                int probeLimit = options.WeakRefProbeSampleLimit <= 0 ? int.MaxValue : options.WeakRefProbeSampleLimit;
                int probesDone = 0;

                foreach ((ulong mt, TypeAggregateIndexEntry entry) in weakRefMtEntries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (probesDone >= probeLimit) break;

                    weakRefObjCount += (int)Math.Min(entry.Count, int.MaxValue);
                    weakRefObjBytes += entry.TotalSize;

                    if (entry.SampleAddress == 0) continue;

                    ClrObject sample = heap.GetObject(entry.SampleAddress);
                    if (!sample.IsValid || sample.Type is null) continue;

                    ClrInstanceField? mHandleField = sample.Type.GetFieldByName("m_handle");
                    if (mHandleField is null) continue;

                    nint handleValue = mHandleField.Read<nint>(entry.SampleAddress, interior: false);
                    probesDone++;
                    if (handleValue == 0)
                    {
                        staleWrapperCount += (int)Math.Min(entry.Count, int.MaxValue);
                        string holderTypeName = sample.Type.Name ?? "Unknown";
                        IncrementDict(staleHolderTypeHits, holderTypeName);
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
                            scanCapped,
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

            var topTargetTypes = BuildTopEntries(targetTypeHits, options.TopTypeLimit);
            var topStaleTypes = BuildTopEntries(staleHolderTypeHits, options.TopTypeLimit);

            return new WeakReferenceDomainResult(
                TotalWeakHandles: totalWeakHandles,
                AliveWeakTargets: aliveWeakTargets,
                DeadWeakTargets: deadWeakTargets,
                DeadTargetRatio: deadRatio,
                WeakHandleKinds: BuildTopEntries(weakHandleKinds, options.TopTypeLimit),
                WeakReferenceObjectCount: weakRefObjCount,
                WeakReferenceObjectBytes: weakRefObjBytes,
                StaleWrapperCount: staleWrapperCount,
                TopWeakTargetTypes: topTargetTypes,
                TopStaleWrapperHolderTypes: topStaleTypes,
                DependentHandleDeadKeyCount: dependentHandleDeadKeyCount,
                ScanCapped: scanCapped,
                ScanCapUsed: options.HandleScanCap,
                PhaseBFallbackUsed: phaseBFallbackUsed,
                PhaseBSkipped: phaseBSkipped,
                Artifacts: rawExports);
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

        private static List<NameCountEntry> BuildTopEntries(Dictionary<string, int> source, int take)
        {
            var list = new List<NameCountEntry>(Math.Min(source.Count, take));
            foreach (KeyValuePair<string, int> kv in source
                         .OrderByDescending(static x => x.Value)
                         .Take(take))
            {
                list.Add(new NameCountEntry(kv.Key, kv.Value));
            }
            return list;
        }

        public void Dispose() { }
    }
}
