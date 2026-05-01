using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers
{
    /// <summary>
    /// Phase-2 analyzer covering §24.1–§24.3 (weak GC handle population, WeakReference&lt;T&gt;
    /// object analysis, and ConditionalWeakTable dead-key analysis).
    ///
    /// Reads <c>HandleSnapshot.bin</c> when the disk index is available to avoid a second
    /// <c>runtime.EnumerateHandles()</c> call. Falls back to live enumeration in memory mode.
    /// Bounded to <see cref="HandleScanCap"/> handles to stay predictable on large dumps.
    /// </summary>
    public sealed class WeakReferenceAnalyzer : IAnalyzer
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const int HandleScanCap  = 50_000;
        private const int TopTypeLimit   = 15;

        // ClrHandleKind enum values (Microsoft.Diagnostics.Runtime):
        //   WeakShort = 0, WeakLong = 1, Strong = 2, Pinned = 3,
        //   RefCounted = 5, Dependent = 6, AsyncPinned = 7, SizedRef = 8, WeakWinRT = 9
        private const byte KindWeakShort  = 0;
        private const byte KindWeakLong   = 1;
        private const byte KindDependent  = 6;
        private const byte KindWeakWinRT  = 9;

        private const string WeakRefGenericName   = "System.WeakReference`1";
        private const string WeakRefNonGenericName = "System.WeakReference";

        // HandleSnapshot.bin record size: Address(8) | MT(8) | Kind(1) | Pad(3) = 20 bytes
        private const int RecordSize = 20;

        public string Name     => "Weak Reference Analysis";
        public string Category => "Memory";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(
            AnalysisContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Analyze(context.Runtime, context.Heap, context.Cache, context.Progress, cancellationToken).Stamp(this));
        }

        private static AnalyzerDomainResult Analyze(
            ClrRuntime runtime,
            ClrHeap heap,
            IHeapAnalysisCache cache,
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
            int deadWeakTargets  = 0;
            bool scanCapped      = false;

            var targetTypeHits = new Dictionary<string, int>(StringComparer.Ordinal);

            // Try disk-backed HandleSnapshot.bin first
            bool handledViaFile = false;
            if (heapIndex is not null
                && heapIndex.StorageKind == HeapIndexStorageKind.Disk
                && heapIndex.IndexPath.Length > 0)
            {
                string indexDir       = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
                string snapshotPath   = Path.Combine(indexDir, DumpIndexPaths.HandleSnapshotFile);

                if (File.Exists(snapshotPath))
                {
                    handledViaFile = true;
                    ReadWeakHandlesFromFile(
                        snapshotPath, heap,
                        ref totalWeakHandles, ref aliveWeakTargets, ref deadWeakTargets,
                        ref scanCapped, targetTypeHits,
                        cancellationToken);
                }
            }

            // Fallback: live enumeration (memory mode or missing snapshot)
            if (!handledViaFile)
            {
                var scanCounter = new ObjectScanCounter("scanning weak handles", progress,
                    reportEveryObjects: 1000, reportEveryElapsed: TimeSpan.FromSeconds(1));

                foreach (ClrHandle handle in runtime.EnumerateHandles())
                {
                    scanCounter.Tick();
                    cancellationToken.ThrowIfCancellationRequested();

                    ClrHandleKind kind = handle.HandleKind;
                    if (kind != ClrHandleKind.WeakShort &&
                        kind != ClrHandleKind.WeakLong  &&
                        kind != ClrHandleKind.WeakWinRT)
                        continue;

                    totalWeakHandles++;
                    if (totalWeakHandles > HandleScanCap) { scanCapped = true; break; }

                    ulong addr = handle.Object.Address;
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
                scanCounter.Complete();
            }

            // ── Phase B: WeakReference<T> object analysis ─────────────────────
            progress?.Report(new(0, "scanning WeakReference objects"));

            int  weakRefObjCount = 0;
            ulong weakRefObjBytes = 0;
            int  staleWrapperCount = 0;
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
                    bool isGenericWR    = name.StartsWith(WeakRefGenericName, StringComparison.Ordinal);
                    bool isNonGenericWR = string.Equals(name, WeakRefNonGenericName, StringComparison.Ordinal);

                    if (isGenericWR || isNonGenericWR)
                        weakRefMtEntries.Add((kv.Key, kv.Value));
                }
            }

            foreach ((ulong mt, TypeAggregateIndexEntry entry) in weakRefMtEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                weakRefObjCount += (int)Math.Min(entry.Count, int.MaxValue);
                weakRefObjBytes += entry.TotalSize;

                // Probe m_handle on a sample to identify stale wrappers.
                // A stale wrapper is a WeakReference whose m_handle IntPtr is zero (collected target).
                if (entry.SampleAddress == 0) continue;

                ClrObject sample = heap.GetObject(entry.SampleAddress);
                if (!sample.IsValid || sample.Type is null) continue;

                ClrInstanceField? mHandleField = sample.Type.GetFieldByName("m_handle");
                if (mHandleField is null) continue;

                // Check each object address we can retrieve from the heap (bounded by entry.Count,
                // but use only a sample because we don't have a full address list here).
                // Use SampleAddress as a representative probe to detect the stale-wrapper pattern.
                nint handleValue = mHandleField.Read<nint>(entry.SampleAddress, interior: false);
                if (handleValue == 0)
                {
                    // Sample itself is stale — approximate all as stale (conservative estimate).
                    staleWrapperCount += (int)Math.Min(entry.Count, int.MaxValue);
                }
            }

            // ── Phase C: Dependent handle dead-key count ──────────────────────
            progress?.Report(new(0, "counting dependent handle dead keys"));

            int dependentHandleDeadKeyCount = 0;

            if (heapIndex is not null
                && heapIndex.StorageKind == HeapIndexStorageKind.Disk
                && heapIndex.IndexPath.Length > 0)
            {
                string indexDir     = Path.GetDirectoryName(heapIndex.IndexPath) ?? string.Empty;
                string snapshotPath = Path.Combine(indexDir, DumpIndexPaths.HandleSnapshotFile);

                if (File.Exists(snapshotPath))
                    dependentHandleDeadKeyCount = CountDependentHandleDeadKeys(snapshotPath, heap, cancellationToken);
            }
            else
            {
                // Fallback: enumerate live handles
                foreach (ClrHandle handle in runtime.EnumerateHandles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (handle.HandleKind != ClrHandleKind.Dependent) continue;

                    ulong addr = handle.Object.Address;
                    if (addr == 0) { dependentHandleDeadKeyCount++; continue; }

                    ClrObject obj = heap.GetObject(addr);
                    if (!obj.IsValid) dependentHandleDeadKeyCount++;
                }
            }

            // ── Build output ──────────────────────────────────────────────────
            double deadRatio = totalWeakHandles == 0
                ? 0.0
                : (double)deadWeakTargets / totalWeakHandles;

            var topTargetTypes = BuildTopEntries(targetTypeHits, TopTypeLimit);
            var topStaleTypes  = BuildTopEntries(staleHolderTypeHits, TopTypeLimit);

            return new WeakReferenceDomainResult(
                TotalWeakHandles:               totalWeakHandles,
                AliveWeakTargets:               aliveWeakTargets,
                DeadWeakTargets:                deadWeakTargets,
                DeadTargetRatio:                deadRatio,
                WeakReferenceObjectCount:       weakRefObjCount,
                WeakReferenceObjectBytes:       weakRefObjBytes,
                StaleWrapperCount:              staleWrapperCount,
                TopWeakTargetTypes:             topTargetTypes,
                TopStaleWrapperHolderTypes:     topStaleTypes,
                DependentHandleDeadKeyCount:    dependentHandleDeadKeyCount,
                ScanCapped:                     scanCapped);
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
                if (totalWeakHandles > HandleScanCap) { scanCapped = true; return; }

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
    }
}
