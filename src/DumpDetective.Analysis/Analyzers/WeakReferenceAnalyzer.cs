using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Indexing.Satellite;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

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

        // HandleSnapshot.bin record size: Address(8) | MT(8) | Kind(1) | Pad(3) = 20 bytes
        private const int RecordSize = 20;

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

            // ── Phase A: Weak handle liveness ─────────────────────────────────
            progress?.Report(new(0, "analysing weak handles"));

            int totalWeakHandles = 0;
            int aliveWeakTargets = 0;
            int deadWeakTargets = 0;
            bool scanCapped = false;

            var targetTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var weakHandleKinds = new Dictionary<string, int>(StringComparer.Ordinal);

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
                    // Only consider weak kinds
                    if (rec.Kind != KindWeakShort && rec.Kind != KindWeakLong && rec.Kind != KindWeakWinRT) continue;

                    totalWeakHandles++;
                    IncrementDict(weakHandleKinds, KindToName(rec.Kind));
                    if (totalWeakHandles > options.HandleScanCap) { scanCapped = true; break; }

                    ulong addr = rec.Addr;
                    if (addr == 0) { deadWeakTargets++; continue; }

                    ClrObject obj = heap.GetObject(addr);
                    if (obj.IsValid)
                    {
                        aliveWeakTargets++;
                        string typeName = obj.Type?.Name ?? "Unknown";
                        IncrementDict(targetTypeHits, typeName);
                    }
                    else
                    {
                        deadWeakTargets++;
                    }
                    if (options.ProduceRawExports)
                        WriteExportRecord(rec.Addr, rec.Mt, rec.Kind);
                }
            }
            else
            {
                // Otherwise try disk-backed snapshot, or enumerate live handles via a memory reader.
                string? snapshotPath = null;
                if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk && heapIndex.IndexPath.Length > 0)
                {
                    string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
                    snapshotPath = Path.Combine(indexDir, DumpIndexPaths.HandleSnapshotFile);
                }

                IHandleSnapshotReader? reader = (snapshotPath is not null && File.Exists(snapshotPath))
                    ? new DiskHandleSnapshotReader(snapshotPath)
                    : HandleSnapshotProvider.CreateMemoryReader(runtime, heap, options.HandleScanCap);

                var scanCounter = new ObjectScanCounter("scanning weak handles", progress,
                    reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

                if (reader is not null)
                {
                    using (reader)
                    {
                        foreach (var rec in reader.EnumerateRecords(cancellationToken))
                        {
                            scanCounter.Tick();
                            cancellationToken.ThrowIfCancellationRequested();

                            if (rec.Kind != KindWeakShort && rec.Kind != KindWeakLong && rec.Kind != KindWeakWinRT) continue;

                            totalWeakHandles++;
                            IncrementDict(weakHandleKinds, KindToName(rec.Kind));
                            if (totalWeakHandles > options.HandleScanCap) { scanCapped = true; break; }

                            ulong addr = rec.Address;
                            if (addr == 0) { deadWeakTargets++; continue; }

                            ClrObject obj = heap.GetObject(addr);
                            if (obj.IsValid)
                            {
                                aliveWeakTargets++;
                                string typeName = obj.Type?.Name ?? "Unknown";
                                IncrementDict(targetTypeHits, typeName);
                            }
                            else
                            {
                                deadWeakTargets++;
                            }
                            if (options.ProduceRawExports)
                                WriteExportRecord(rec.Address, rec.MethodTable, rec.Kind);
                        }
                        scanCounter.Complete();
                    }
                    try { tmpGz?.Dispose(); tmpGz = null; tmpFs = null; }
                    catch { }
                }
            }

            // ── Phase B: WeakReference<T> object analysis ─────────────────────
            progress?.Report(new(0, "scanning WeakReference objects"));

            int weakRefObjCount = 0;
            ulong weakRefObjBytes = 0;
            int staleWrapperCount = 0;
            var staleHolderTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);

            // Find WeakReference MT candidates from TypeAggregates
            var weakRefMtEntries = new List<(ulong Mt, TypeAggregateIndexEntry Entry)>(4);

            if (typeAggregates is not null)
            {
                foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in typeAggregates)
                {
                    ClrType? clrType = heap.GetTypeByMethodTable(kv.Key);
                    if (clrType is null) continue;

                    string? name = clrType.Name;
                    if (name is null) continue;

                    // Match "System.WeakReference`1[...]" or "System.WeakReference"
                    bool isGenericWR = name.StartsWith(WeakRefGenericName, StringComparison.Ordinal);
                    bool isNonGenericWR = string.Equals(name, WeakRefNonGenericName, StringComparison.Ordinal);

                    if (isGenericWR || isNonGenericWR)
                        weakRefMtEntries.Add((kv.Key, kv.Value));
                }
            }

            // Bound the number of sample probes to avoid heavy per-type work.
            int probeLimit = options.WeakRefProbeSampleLimit <= 0 ? int.MaxValue : options.WeakRefProbeSampleLimit;
            int probesDone = 0;

            foreach ((ulong mt, TypeAggregateIndexEntry entry) in weakRefMtEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Respect the configured probe cap.
                if (probesDone >= probeLimit) break;

                weakRefObjCount += (int)Math.Min(entry.Count, int.MaxValue);
                weakRefObjBytes += entry.TotalSize;

                // Probe m_handle on a sample to identify stale wrappers.
                // A stale wrapper is a WeakReference whose m_handle IntPtr is zero (collected target).
                if (entry.SampleAddress == 0) continue;

                ClrObject sample = heap.GetObject(entry.SampleAddress);
                if (!sample.IsValid || sample.Type is null) continue;

                ClrInstanceField? mHandleField = sample.Type.GetFieldByName("m_handle");
                if (mHandleField is null) continue;

                // Check a sample instance's m_handle field to detect stale wrappers.
                // This probe is intentionally lightweight; we cap the number of probes with
                // `WeakRefProbeSampleLimit` in the preset/options.
                nint handleValue = mHandleField.Read<nint>(entry.SampleAddress, interior: false);
                probesDone++;
                if (handleValue == 0)
                {
                    // Sample itself is stale — approximate all as stale (conservative estimate).
                    staleWrapperCount += (int)Math.Min(entry.Count, int.MaxValue);
                }
            }

            // ── Phase C: Dependent handle dead-key count ──────────────────────
            progress?.Report(new(0, "counting dependent handle dead keys"));

            int dependentHandleDeadKeyCount = 0;

            if (heapIndex is not null && heapIndex.InMemoryHandleSnapshot is { Length: > 0 } inMemHandles)
            {
                foreach (var rec in inMemHandles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rec.Kind != KindDependent) continue;
                    ulong addr = rec.Addr;
                    if (addr == 0) { dependentHandleDeadKeyCount++; continue; }
                    ClrObject obj = heap.GetObject(addr);
                    if (!obj.IsValid) dependentHandleDeadKeyCount++;
                }
            }
            else
            {
                string? snapshotPath = null;
                if (heapIndex is not null && heapIndex.StorageKind == HeapIndexStorageKind.Disk && heapIndex.IndexPath.Length > 0)
                {
                    string indexDir = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
                    snapshotPath = Path.Combine(indexDir, DumpIndexPaths.HandleSnapshotFile);
                }

                IHandleSnapshotReader? reader = (snapshotPath is not null && File.Exists(snapshotPath))
                    ? new DiskHandleSnapshotReader(snapshotPath)
                    : HandleSnapshotProvider.CreateMemoryReader(runtime, heap, options.HandleScanCap);

                if (reader is not null)
                {
                    using (reader)
                    {
                        foreach (var rec in reader.EnumerateRecords(cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (rec.Kind != KindDependent) continue;
                            ulong addr = rec.Address;
                            if (addr == 0) { dependentHandleDeadKeyCount++; continue; }
                            ClrObject obj = heap.GetObject(addr);
                            if (!obj.IsValid) dependentHandleDeadKeyCount++;
                            if (options.ProduceRawExports)
                                WriteExportRecord(rec.Address, rec.MethodTable, rec.Kind);
                        }
                    }
                    try { tmpGz?.Dispose(); tmpGz = null; tmpFs = null; }
                    catch { }
                }
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
                RawExports: rawExports);
        }

        // ── File reader helpers ───────────────────────────────────────────────

        private static void ReadWeakHandlesFromFile(
            string filePath,
            ClrHeap heap,
            ref int totalWeakHandles,
            ref int aliveWeakTargets,
            ref int deadWeakTargets,
            ref bool scanCapped,
            Dictionary<string, int> targetTypeHits,
            int handleScanCap,
            CancellationToken cancellationToken)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return;

            Span<byte> rec = stackalloc byte[RecordSize];
            for (long i = 0; i < header.RecordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                if (read < RecordSize) break;

                byte kind = rec[16];
                if (kind != KindWeakShort && kind != KindWeakLong && kind != KindWeakWinRT)
                    continue;

                totalWeakHandles++;
                if (totalWeakHandles > handleScanCap) { scanCapped = true; return; }

                ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                if (addr == 0) { deadWeakTargets++; continue; }

                ClrObject obj = heap.GetObject(addr);
                if (obj.IsValid)
                {
                    aliveWeakTargets++;
                    string typeName = obj.Type?.Name ?? "Unknown";
                    IncrementDict(targetTypeHits, typeName);
                }
                else
                {
                    deadWeakTargets++;
                }
            }
        }

        private static int CountDependentHandleDeadKeys(
            string filePath,
            ClrHeap heap,
            CancellationToken cancellationToken)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4 * 1024, FileOptions.SequentialScan);

            if (!IndexHeader.TryRead(stream, out IndexHeader header))
                return 0;

            int deadCount = 0;
            Span<byte> rec = stackalloc byte[RecordSize];

            for (long i = 0; i < header.RecordCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = stream.ReadAtLeast(rec, RecordSize, throwOnEndOfStream: false);
                if (read < RecordSize) break;

                if (rec[16] != KindDependent) continue;

                ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(rec);
                if (addr == 0) { deadCount++; continue; }

                ClrObject obj = heap.GetObject(addr);
                if (!obj.IsValid) deadCount++;
            }

            return deadCount;
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
