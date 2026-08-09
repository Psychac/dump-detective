using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Analyze managed string usage: counts, sizes, LOH/FOH stats and duplicate patterns.
/// Prefers pre-built string dedup index when available to avoid random dump I/O.
/// </summary>
internal sealed class StringAnalyzer : IAnalyzer, IParallelHeapIndexScanParticipant
{
    // File-level constants removed. Use StringAnalysisOptions for configurable thresholds.
    /// <inheritdoc/>
    public string Name => "String Analysis";

    /// <inheritdoc/>
    public string Category => "Memory";

    // Field layout cache per MethodTable to avoid re-enumerating ClrType.Fields
    private sealed class FieldLayoutCache
    {
        private readonly Dictionary<ulong, ClrInstanceField[]> _cache = new();

        public ClrInstanceField[] GetFields(ClrType? type)
        {
            if (type == null) return [];

            var mt = type.MethodTable;
            if (!_cache.TryGetValue(mt, out var fields))
            {
                fields = type.Fields.ToArray();
                _cache[mt] = fields;
            }
            return fields;
        }
    }

    // Instance accumulator state for the index-scan dedup branch of the
    // IHeapIndexScanParticipant path. Populated by BeforeHeapIndexScan (called by the
    // pipeline dispatcher) and mutated per-entry by OnHeapEntry; consumed by AnalyzeAsync
    // once the shared index scan has completed. Every other branch of Analyze (prebuilt
    // dedup, FOH interned scan, no-index fallbacks) is untouched by this state.
    private ClrHeap? _heap;
    private StringAnalysisOptions? _indexScanStringOptions;
    private HashSet<ulong>? _indexScanStringMts;
    private bool _indexScanDedupActive;
    private int _indexScanMaxToDedup;
    private int _indexScanMaxUnique;
    private Dictionary<StringFingerprint, StringLeakInfo>? _indexScanStringStats;
    private Dictionary<ulong, int>? _indexScanMethodTableDupCounts;
    private List<int>? _indexScanLengthSamples;
    private Dictionary<string, int>? _indexScanLengthBuckets;
    private List<LongStringEntry>? _indexScanVeryLongStrings;
    private int _indexScanStringsRead;

    // P1-3 accumulator state: string-field-ownership tracking. Independent of
    // _indexScanDedupActive (runs even when dedup itself is skipped) — piggybacks on the
    // same shared disk-index pass rather than doing its own heap.EnumerateObjects() walk.
    private const int MaxStringOwnerTypesToTrack = 100;
    private bool _indexScanOwnerTypesActive;
    private FieldLayoutCache? _indexScanFieldCache;
    private Dictionary<ulong, int[]>? _indexScanStringOwnerFieldIndices; // owner MT -> string field indices
    private HashSet<ulong>? _indexScanTypesWithoutStringFields; // negative cache
    private Dictionary<ulong, ulong>? _indexScanStringOwnerTypeBytes; // owner MT -> total string bytes owned

    /// <summary>
    /// Resolves whether the index-scan dedup branch will run this pass (mirroring the
    /// same DeduplicationMode/prebuilt-availability decision made inline in
    /// <see cref="Analyze"/>) and, if so, seeds the accumulator fields consumed by
    /// <see cref="OnHeapEntry"/> and read back in <see cref="Analyze"/>.
    /// </summary>
    public void BeforeHeapIndexScan(AnalysisContext context)
    {
        ClrHeap heap = context.Heap;
        _heap = heap;
        StringAnalysisOptions stringOptions = context.AnalysisOptions.StringAnalysis;
        _indexScanStringOptions = stringOptions;

        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        HeapIndexBuildResult? heapIndex = null;
        var stringMts = new HashSet<ulong>(capacity: 4);
        int totalStrings = 0;

        if (context.Cache is HeapAnalysisCache concreteCache && concreteCache.TryGetHeapIndex(out heapIndex))
        {
            typeAggregates = heapIndex.TypeAggregates;
            foreach (var kvp in heapIndex.TypeAggregates)
            {
                if ((kvp.Value.Flags & TypeAggregateFlags.IsStringType) == 0) continue;
                stringMts.Add(kvp.Key);
                totalStrings += (int)Math.Min(kvp.Value.Count, int.MaxValue);
            }
        }

        _indexScanStringMts = stringMts;

        // P1-3: seed owner-type tracking whenever there are string types to look for, independent
        // of whether dedup itself runs — this reuses the same shared disk-index pass.
        _indexScanOwnerTypesActive = stringMts.Count > 0;
        if (_indexScanOwnerTypesActive)
        {
            _indexScanFieldCache = new FieldLayoutCache();
            _indexScanStringOwnerFieldIndices = new Dictionary<ulong, int[]>(capacity: 1000);
            _indexScanTypesWithoutStringFields = new HashSet<ulong>(capacity: 1000);
            _indexScanStringOwnerTypeBytes = new Dictionary<ulong, ulong>(capacity: MaxStringOwnerTypesToTrack);
        }
        else
        {
            _indexScanFieldCache = null;
            _indexScanStringOwnerFieldIndices = null;
            _indexScanTypesWithoutStringFields = null;
            _indexScanStringOwnerTypeBytes = null;
        }

        bool runDedup = stringOptions.EnableDeduplication
            && stringOptions.DeduplicationMode != DeduplicationMode.Disabled
            && totalStrings <= stringOptions.DeduplicationStringCountThreshold;

        var prebuilt = heapIndex?.StringDedupIndex;
        bool active = runDedup
            && stringOptions.DeduplicationMode == DeduplicationMode.FallbackToHeapScan
            && (prebuilt is null || prebuilt.Count == 0)
            && typeAggregates is not null;

        _indexScanDedupActive = active;
        if (!active)
        {
            _indexScanStringStats = null;
            _indexScanMethodTableDupCounts = null;
            _indexScanLengthSamples = null;
            _indexScanLengthBuckets = null;
            _indexScanVeryLongStrings = null;
            _indexScanStringsRead = 0;
            return;
        }

        (int maxToDedup, int maxUnique) = ComputeEffectiveCaps(stringOptions, stringOptions.MaxStringsToDedup, stringOptions.MaxUniqueStringTracking);
        _indexScanMaxToDedup = maxToDedup;
        _indexScanMaxUnique = maxUnique;
        _indexScanStringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
        _indexScanMethodTableDupCounts = new Dictionary<ulong, int>(capacity: 64);
        _indexScanLengthSamples = new List<int>(capacity: 100_000);
        _indexScanLengthBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["0-15"] = 0,
            ["16-31"] = 0,
            ["32-63"] = 0,
            ["64-127"] = 0,
            ["128-255"] = 0,
            ["256-511"] = 0,
            ["512-1023"] = 0,
            ["1024-4095"] = 0,
            ["4096-16383"] = 0,
            ["16384-65535"] = 0,
            ["65536+"] = 0
        };
        _indexScanVeryLongStrings = new List<LongStringEntry>(capacity: 16);
        _indexScanStringsRead = 0;
    }

    /// <summary>
    /// Called once per disk-backed index entry, in address order, during the shared
    /// heap-index scan pass. Mirrors the historical index-scan dedup loop body, operating
    /// on instance fields. No explicit-interface forwarder needed: both this class and
    /// <see cref="HeapEntry"/> are internal.
    /// </summary>
    public void OnHeapEntry(in HeapEntry entry)
    {
        if (_indexScanOwnerTypesActive)
            AccumulateStringOwnerType(in entry);

        if (!_indexScanDedupActive) return;

        StringAnalysisOptions stringOptions = _indexScanStringOptions!;
        if (!IsStringMt(_heap!, entry.MethodTable, _indexScanStringMts!)) return;

        if (entry.Size >= (ulong)stringOptions.VeryLongStringThresholdBytes)
        {
            int ecl = (int)Math.Min((entry.Size - 26) / 2, int.MaxValue);
            _indexScanVeryLongStrings!.Add(new LongStringEntry(entry.Address, ecl, entry.Size, Preview: null, TypeName: null));
        }

        if (_indexScanStringsRead >= _indexScanMaxToDedup) return;
        if (!IsStringSizeInBounds(entry.Size, stringOptions)) return;
        _indexScanStringsRead++;
        FingerprintAddress(_heap!, entry.Address, entry.Size, stringOptions, _indexScanStringStats!, _indexScanMaxUnique, _indexScanMethodTableDupCounts!, _indexScanLengthSamples!, _indexScanLengthBuckets!, samplingSource: "IndexScan");
    }

    /// <summary>
    /// P1-3: accumulate string-field bytes owned by <paramref name="entry"/>'s type, using
    /// the entry's MethodTable (already known from the disk index — no memory read required)
    /// to look up cached string-field indices before ever touching the object itself. Objects
    /// of types with no string fields (the overwhelming majority) cost one HashSet lookup and
    /// nothing else — <c>heap.GetObject</c> is only called once we know the type is relevant.
    /// </summary>
    private void AccumulateStringOwnerType(in HeapEntry entry)
    {
        ulong mt = entry.MethodTable;

        if (_indexScanTypesWithoutStringFields!.Contains(mt))
            return;

        if (!_indexScanStringOwnerFieldIndices!.TryGetValue(mt, out int[]? stringFieldIndices))
        {
            ClrType? type = _heap!.GetTypeByMethodTable(mt);
            if (type is null)
            {
                _indexScanTypesWithoutStringFields.Add(mt);
                return;
            }

            var fields = _indexScanFieldCache!.GetFields(type);
            var indices = new List<int>(capacity: 4);
            for (int i = 0; i < fields.Length; i++)
            {
                try
                {
                    ClrType? fieldType = fields[i].Type;
                    if (fieldType != null && _indexScanStringMts!.Contains(fieldType.MethodTable))
                        indices.Add(i);
                }
                catch
                {
                    // Skip malformed field definitions
                }
            }

            if (indices.Count == 0)
            {
                _indexScanTypesWithoutStringFields.Add(mt);
                return;
            }

            stringFieldIndices = indices.ToArray();
            _indexScanStringOwnerFieldIndices[mt] = stringFieldIndices;
        }

        var ownerBytes = _indexScanStringOwnerTypeBytes!;
        bool alreadyTracked = ownerBytes.ContainsKey(mt);
        if (!alreadyTracked && ownerBytes.Count >= MaxStringOwnerTypesToTrack)
            return; // cap reached — only keep accumulating types already being tracked

        ClrObject obj = _heap!.GetObject(entry.Address);
        if (!obj.IsValid) return;

        var objFields = _indexScanFieldCache!.GetFields(obj.Type);
        ulong addBytes = 0;
        foreach (int fieldIndex in stringFieldIndices)
        {
            try
            {
                if (fieldIndex >= objFields.Length) continue;
                ClrObject stringRef = objFields[fieldIndex].ReadObject(obj, interior: false);
                if (!stringRef.IsValid) continue;
                addBytes += stringRef.Size;
            }
            catch
            {
                // Skip malformed field reads
            }
        }

        if (addBytes > 0)
        {
            ownerBytes.TryGetValue(mt, out ulong existing);
            ownerBytes[mt] = existing + addBytes;
        }
    }

    IHeapIndexScanParticipant IParallelHeapIndexScanParticipant.CreateWorkerInstance() =>
        new StringAnalyzer();

    // Workers covered disjoint address ranges; merge their dedup state into this instance.
    // If _indexScanDedupActive is false (dedup was skipped on this context), there's nothing
    // to merge — workers whose dedup was also inactive contribute nothing.
    void IParallelHeapIndexScanParticipant.MergePartial(IReadOnlyList<IHeapIndexScanParticipant> partials)
    {
        // P1-3 owner-type bytes: independent of dedup active state, merged separately.
        if (_indexScanOwnerTypesActive)
        {
            var ownerBytes = _indexScanStringOwnerTypeBytes!;
            foreach (IHeapIndexScanParticipant p in partials)
            {
                var other = (StringAnalyzer)p;
                if (!other._indexScanOwnerTypesActive) continue;

                foreach (var kvp in other._indexScanStringOwnerTypeBytes!)
                {
                    if (!ownerBytes.ContainsKey(kvp.Key) && ownerBytes.Count >= MaxStringOwnerTypesToTrack)
                        continue;
                    ownerBytes.TryGetValue(kvp.Key, out ulong existing);
                    ownerBytes[kvp.Key] = existing + kvp.Value;
                }
            }
        }

        if (!_indexScanDedupActive) return;

        var stringStats = _indexScanStringStats!;
        var mtDups = _indexScanMethodTableDupCounts!;
        var lengthSamples = _indexScanLengthSamples!;
        var buckets = _indexScanLengthBuckets!;
        var longStrings = _indexScanVeryLongStrings!;

        foreach (IHeapIndexScanParticipant p in partials)
        {
            var other = (StringAnalyzer)p;
            if (!other._indexScanDedupActive) continue;

            _indexScanStringsRead += other._indexScanStringsRead;

            // Fingerprint map: sum Count and TotalSize per fingerprint, respect _indexScanMaxUnique.
            foreach (var kvp in other._indexScanStringStats!)
            {
                ref StringLeakInfo self = ref CollectionsMarshal.GetValueRefOrAddDefault(
                    stringStats, kvp.Key, out bool existed);
                if (!existed)
                {
                    if (stringStats.Count > _indexScanMaxUnique)
                    {
                        stringStats.Remove(kvp.Key);
                        continue;
                    }
                    self = kvp.Value;
                }
                else
                {
                    self.Count += kvp.Value.Count;
                    self.TotalSize += kvp.Value.TotalSize;
                    if (self.SampleAddresses is null)
                        self.SampleAddresses = kvp.Value.SampleAddresses;
                }
            }

            // Method-table dup counts: sum per key.
            foreach (var kvp in other._indexScanMethodTableDupCounts!)
            {
                mtDups.TryGetValue(kvp.Key, out int existing);
                mtDups[kvp.Key] = existing + kvp.Value;
            }

            // Length samples: just concat (used for statistical percentile computation).
            lengthSamples.AddRange(other._indexScanLengthSamples!);

            // Bucket counts: sum per bucket label.
            foreach (var kvp in other._indexScanLengthBuckets!)
            {
                buckets.TryGetValue(kvp.Key, out int existing);
                buckets[kvp.Key] = existing + kvp.Value;
            }

            // Very-long-string list: concat (no ordering required).
            longStrings.AddRange(other._indexScanVeryLongStrings!);
        }
    }

    /// <summary>
    /// Analyze the provided <see cref="AnalysisContext"/> and return a <see cref="AnalyzerDomainResult"/>.
    /// This method is the I/O entry point and delegates to internal helpers.
    /// </summary>
    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StringAnalysisOptions stringOptions = context.AnalysisOptions.StringAnalysis;
        ulong totalManagedBytes = GetTotalManagedBytes(context);
        List<(ulong Start, ulong End)> fohSegments = BuildFohSegments(context.Heap);

        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, stringOptions, totalManagedBytes, fohSegments, context.Progress).Stamp(this));
    }

    /// <summary>
    /// Core analysis implementation. Separated for easier unit testing and to keep
    /// the public entry point small.
    /// </summary>
    private AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        StringAnalysisOptions stringOptions,
        ulong totalManagedBytes,
        List<(ulong Start, ulong End)> fohSegments,
        IProgress<AnalyzerProgressReport>? progress)
    {
        var totalStopwatch = Stopwatch.StartNew();

        // ── Resolve TypeAggregates once ──────────────────────────────────────────────────
        var stringMts = new HashSet<ulong>(capacity: 4);
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;
        HeapIndexBuildResult? heapIndex = null;

        if (cache is HeapAnalysisCache concreteCache && concreteCache.TryGetHeapIndex(out heapIndex))
        {
            typeAggregates = heapIndex.TypeAggregates;
            foreach (var kvp in heapIndex.TypeAggregates)
                if ((kvp.Value.Flags & TypeAggregateFlags.IsStringType) != 0)
                    stringMts.Add(kvp.Key);
        }

        // ── Scalar stats: derive from TypeAggregates when available — zero heap scan ────
        int totalStrings = 0;
        ulong totalStringMemory = 0;
        ulong lohStringBytes = 0;
        long gen2StringCount = 0;
        ulong gen2StringBytes = 0;
        var veryLongStrings = new List<LongStringEntry>(capacity: 16);

        if (typeAggregates is not null && stringMts.Count > 0)
        {
            foreach (ulong mt in stringMts)
            {
                if (!typeAggregates.TryGetValue(mt, out TypeAggregateIndexEntry entry)) continue;
                totalStrings += (int)Math.Min(entry.Count, int.MaxValue);
                totalStringMemory += entry.TotalSize;
                lohStringBytes += entry.LohSize;
                gen2StringCount += entry.Gen2Count;
                if (entry.Count > 0)
                    gen2StringBytes += (ulong)entry.Gen2Count * (entry.TotalSize / (ulong)entry.Count);
            }
            progress?.Report(new(totalStrings, "string stats from index", $"{totalStrings:N0} strings, {FormatBytes(totalStringMemory)} total"));
        }

        // ── Interned strings: scan only FOH segments (tiny — typically 1–2 segments)
        int internedStringCount = 0;
        ulong internedStringBytes = 0;
        if (stringOptions.DetectInterning && fohSegments.Count > 0)
        {
            foreach (ClrSegment segment in heap.Segments)
            {
                if (!IsSegmentInFoh(segment, fohSegments)) continue;
                foreach (ClrObject obj in segment.EnumerateObjects())
                {
                    if (!obj.IsValid || obj.IsFree) continue;
                    if (!IsStringMt(heap, obj.Type?.MethodTable ?? 0, stringMts)) continue;
                    internedStringCount++;
                    internedStringBytes += obj.Size;
                }
            }
        }

        // ── Deduplication: pre-built-index or bounded content scan — only when enabled and within threshold ─
        var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
        var methodTableDupCounts = new Dictionary<ulong, int>(capacity: 64);
        bool dedupSkipped = false;

        bool runDedup = stringOptions.EnableDeduplication
            && stringOptions.DeduplicationMode != DeduplicationMode.Disabled
            && totalStrings <= stringOptions.DeduplicationStringCountThreshold;

        if (!runDedup && totalStrings > 0)
        {
            dedupSkipped = true;
            progress?.Report(new(totalStrings, "string dedup skipped",
                $"{totalStrings:N0} strings exceed threshold ({stringOptions.DeduplicationStringCountThreshold:N0}) or dedup disabled. Set EnableDeduplication=true and appropriate DeduplicationMode to enable."));
        }

        int stringsSampled = 0;
        string? dedupSource = null;
        // length sampling structures
        var lengthSamples = new List<int>(capacity: 100_000);
        var lengthBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["0-15"] = 0,
            ["16-31"] = 0,
            ["32-63"] = 0,
            ["64-127"] = 0,
            ["128-255"] = 0,
            ["256-511"] = 0,
            ["512-1023"] = 0,
            ["1024-4095"] = 0,
            ["4096-16383"] = 0,
            ["16384-65535"] = 0,
            ["65536+"] = 0
        };
        if (runDedup)
        {
            // Compute effective numeric caps based on sampling mode and configured values.
            (int maxToDedup, int maxUnique) = ComputeEffectiveCaps(stringOptions, stringOptions.MaxStringsToDedup, stringOptions.MaxUniqueStringTracking);

            var prebuilt = heapIndex?.StringDedupIndex;

            // Dedup path selection based on DeduplicationMode
            // ── PreferPrebuiltOnly: only use prebuilt index if present, otherwise skip
            // ── FallbackToHeapScan: prefer prebuilt, else index-backed scan, else full heap scan
            // ── Disabled handled above via runDedup flag

            if (stringOptions.DeduplicationMode == DeduplicationMode.PreferPrebuiltOnly)
            {
                if (prebuilt is not null && prebuilt.Count > 0)
                {
                    foreach (var kvp in prebuilt)
                    {
                        if (kvp.Value.Count <= 1) continue; // singletons aren't duplicates
                        var fp = new StringFingerprint(kvp.Key, 0, '\0', '\0');
                        if (!stringStats.ContainsKey(fp) && stringStats.Count >= maxUnique) continue;
                        ref StringLeakInfo entry = ref CollectionsMarshal.GetValueRefOrAddDefault(stringStats, fp, out bool existed);
                        if (!existed)
                        {
                            entry.Preview = kvp.Value.Preview;
                            entry.SampleAddresses = kvp.Value.SampleAddresses;
                            entry.DominantMethodTable = kvp.Value.DominantMethodTable;
                            entry.FingerprintHash = kvp.Key;
                            entry.SamplingSource = "Prebuilt";
                        }
                        entry.Count += kvp.Value.Count;
                        entry.TotalSize += kvp.Value.TotalSize;
                        if (entry.DominantMethodTable != 0)
                        {
                            methodTableDupCounts.TryGetValue(entry.DominantMethodTable, out int c);
                            methodTableDupCounts[entry.DominantMethodTable] = c + kvp.Value.Count;
                        }
                    }
                    progress?.Report(new(totalStrings, "string dedup complete",
                        $"{stringStats.Count:N0} duplicate patterns from pre-built index ({prebuilt.Count:N0} unique strings scanned during index build)"));
                    stringsSampled = prebuilt.Count;
                }
                else
                {
                    // Prebuilt required but missing.
                    dedupSkipped = true;
                    progress?.Report(new(totalStrings, "string dedup skipped",
                        "Deduplication mode set to PreferPrebuiltOnly but no prebuilt index was found; skipping dedup."));
                    stringsSampled = 0;
                }
            }
            else
            {
                // FallbackToHeapScan behaviour (existing): prefer prebuilt, else index scan, else full heap scan
                // ── Fast path: use pre-built dedup index from heap scan (zero dump I/O) ──────
                if (prebuilt is not null && prebuilt.Count > 0)
                {
                    // Use the prebuilt string dedup index produced at index-build time.
                    // The index key is a 64-bit content hash computed while object pages
                    // were hot; length/char samples are not available here. We therefore
                    // synthesize a `StringFingerprint` that preserves the 64-bit hash
                    // while leaving length/char sentinels unset. The prebuilt index
                    // already groups identical content via the hash, so this is a
                    // fast, zero-I/O way to aggregate duplicate counts and sizes.
                    foreach (var kvp in prebuilt)
                    {
                        if (kvp.Value.Count <= 1) continue; // singletons aren't duplicates
                        var fp = new StringFingerprint(kvp.Key, 0, '\0', '\0');
                        if (!stringStats.ContainsKey(fp) && stringStats.Count >= maxUnique) continue;
                        ref StringLeakInfo entry = ref CollectionsMarshal.GetValueRefOrAddDefault(stringStats, fp, out bool existed);
                        if (!existed)
                        {
                            entry.Preview = kvp.Value.Preview;
                            entry.SampleAddresses = kvp.Value.SampleAddresses;
                            entry.DominantMethodTable = kvp.Value.DominantMethodTable;
                            entry.FingerprintHash = kvp.Key;
                            entry.SamplingSource = "Prebuilt";
                        }
                        else
                        {
                            if (entry.FingerprintHash == 0)
                                entry.FingerprintHash = kvp.Key;
                            if (string.IsNullOrEmpty(entry.SamplingSource))
                                entry.SamplingSource = "Prebuilt";
                        }
                        entry.Count += kvp.Value.Count;
                        entry.TotalSize += kvp.Value.TotalSize;
                        if (entry.DominantMethodTable != 0)
                        {
                            methodTableDupCounts.TryGetValue(entry.DominantMethodTable, out int c);
                            methodTableDupCounts[entry.DominantMethodTable] = c + kvp.Value.Count;
                        }
                    }
                    progress?.Report(new(totalStrings, "string dedup complete",
                        $"{stringStats.Count:N0} duplicate patterns from pre-built index ({prebuilt.Count:N0} unique strings scanned during index build)"));
                    stringsSampled = prebuilt.Count;
                }
                else if (typeAggregates is not null)
                {
                    // Index available but no pre-built dedup (e.g. disk-backed with cached
                    // index). The dedup scan itself already happened as this analyzer's
                    // IHeapIndexScanParticipant.OnHeapEntry during the shared dispatcher
                    // pass (see AnalysisPipeline.ExecuteAsync) — just read the results back.
                    if (_indexScanDedupActive)
                    {
                        veryLongStrings = _indexScanVeryLongStrings!;
                        stringStats = _indexScanStringStats!;
                        methodTableDupCounts = _indexScanMethodTableDupCounts!;
                        lengthSamples = _indexScanLengthSamples!;
                        lengthBuckets = _indexScanLengthBuckets!;
                        stringsSampled = _indexScanStringsRead;
                        progress?.Report(new(totalStrings, "string dedup complete",
                            $"{_indexScanStringsRead:N0} strings sampled from {totalStrings:N0} total"));
                    }
                }
                else
                {
                    // No-index fallback: single pass collecting stats + bounded dedup.
                    int stringsRead = 0;
                    var sc = new ObjectScanCounter("scanning string objects", progress);
                    foreach (ClrObject obj in heap.EnumerateObjects())
                    {
                        sc.Tick();
                        if (!obj.IsValid || obj.Type is null) continue;
                        if (!string.Equals(obj.Type.Name, "System.String", StringComparison.Ordinal)) continue;
                        stringMts.Add(obj.Type.MethodTable);

                        totalStrings++;
                        totalStringMemory += obj.Size;
                        if (obj.Size >= (ulong)stringOptions.LohThresholdBytes) lohStringBytes += obj.Size;
                        if (obj.Size >= (ulong)stringOptions.VeryLongStringThresholdBytes)
                        {
                            int ecl = obj.Size > 26 ? (int)Math.Min((obj.Size - 26) / 2, int.MaxValue) : 0;
                            string? preview = obj.AsString(maxLength: 100);
                            string? typeName = obj.Type?.Name;
                            veryLongStrings.Add(new LongStringEntry(obj.Address, ecl, obj.Size, Preview: preview, TypeName: typeName));
                        }
                        if (stringOptions.DetectInterning && fohSegments.Count > 0 && IsInFoh(obj.Address, fohSegments))
                        { internedStringCount++; internedStringBytes += obj.Size; continue; }

                        if (stringsRead < maxToDedup && IsStringSizeInBounds(obj.Size, stringOptions))
                        {
                            stringsRead++;
                            FingerprintAddress(heap, obj.Address, obj.Size, stringOptions, stringStats, maxUnique, methodTableDupCounts, lengthSamples, lengthBuckets, samplingSource: "HeapScan");
                        }
                    }
                    sc.Complete();
                    stringsSampled = stringsRead;
                }
            }
            // (Dedup handled above in DeduplicationMode-aware branches)
        }
        else if (typeAggregates is null)
        {
            // No index, no dedup: full heap scan for scalar stats only.
            var sc = new ObjectScanCounter("scanning string objects (stats only)", progress);
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                sc.Tick();
                if (!obj.IsValid || obj.Type is null) continue;
                if (!string.Equals(obj.Type.Name, "System.String", StringComparison.Ordinal)) continue;
                totalStrings++;
                totalStringMemory += obj.Size;
                if (obj.Size >= (ulong)stringOptions.LohThresholdBytes) lohStringBytes += obj.Size;
                int ecl = obj.Size > 26 ? (int)Math.Min((obj.Size - 26) / 2, int.MaxValue) : 0;
                if (obj.Size >= (ulong)stringOptions.VeryLongStringThresholdBytes)
                {
                    string? preview = obj.AsString(maxLength: 100);
                    string? typeName = obj.Type?.Name;
                    veryLongStrings.Add(new LongStringEntry(obj.Address, ecl, obj.Size, Preview: preview, TypeName: typeName));
                }
                if (fohSegments.Count > 0 && IsInFoh(obj.Address, fohSegments))
                { internedStringCount++; internedStringBytes += obj.Size; }
                stringMts.Add(obj.Type.MethodTable);
            }
            sc.Complete();
        }

        // ── Aggregate dedup results ──────────────────────────────────────────────────────
        int sampledUniquePatterns = dedupSkipped ? 0 : ComputeUniqueCount(stringStats);
        int duplicatePatternCount = 0;
        ulong duplicateWastedBytes = 0;

        var byWasteHeap = new PriorityQueue<StringLeakInfo, ulong>(stringOptions.TopDuplicatesToShow + 1);
        var byCountHeap = new PriorityQueue<StringLeakInfo, int>(stringOptions.TopDuplicatesToShow + 1);

        int minCount = stringOptions.MinDuplicateStringCount;
        foreach (StringLeakInfo info in stringStats.Values)
        {
            if (info.Count <= minCount) continue;
            duplicatePatternCount++;
            ulong wasted = info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
            duplicateWastedBytes += wasted;
            byWasteHeap.Enqueue(info, info.TotalSize);
            if (byWasteHeap.Count > stringOptions.TopDuplicatesToShow) byWasteHeap.Dequeue();
            byCountHeap.Enqueue(info, info.Count);
            if (byCountHeap.Count > stringOptions.TopDuplicatesToShow) byCountHeap.Dequeue();
        }

        // Prepare a map from dominant method-table -> type name for snapshots
        var mtToName = new Dictionary<ulong, string?>(methodTableDupCounts.Count);
        foreach (var mt in methodTableDupCounts.Keys)
            mtToName[mt] = heap.GetTypeByMethodTable(mt)?.Name;

        IReadOnlyList<DuplicateStringSnapshot> topByWaste = DrainToDescendingWaste(byWasteHeap, mtToName);
        IReadOnlyList<DuplicateStringSnapshot> topByCount = DrainToDescendingCount(byCountHeap, mtToName);
        IReadOnlyList<DuplicateStringSnapshot> topDuplicates = MergeTopDuplicates(topByWaste, topByCount);

        // Build frequency buckets from stringStats
        var freqBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1"] = 0,
            ["2"] = 0,
            ["3-10"] = 0,
            ["11-100"] = 0,
            ["101-1000"] = 0,
            ["1001+"] = 0
        };
        foreach (var info in stringStats.Values)
        {
            int c = info.Count;
            if (c <= 1) freqBuckets["1"]++;
            else if (c == 2) freqBuckets["2"]++;
            else if (c <= 10) freqBuckets["3-10"]++;
            else if (c <= 100) freqBuckets["11-100"]++;
            else if (c <= 1000) freqBuckets["101-1000"]++;
            else freqBuckets["1001+"]++;
        }

        // Compute percentiles and buckets. Prefer index-provided distribution when available.
        IReadOnlyDictionary<string, double> percentiles = new Dictionary<string, double>(StringComparer.Ordinal);
        int sampleCount = lengthSamples.Count;
        var idxDist = heapIndex?.StringDedupDistribution;

        if (idxDist is not null && idxDist.SampleCount > 0)
        {
            percentiles = idxDist.Percentiles ?? new Dictionary<string, double>(StringComparer.Ordinal);
            lengthBuckets = new Dictionary<string, int>(idxDist.LengthBuckets ?? new Dictionary<string, int>());
            freqBuckets = new Dictionary<string, int>(idxDist.FrequencyBuckets ?? freqBuckets);
            sampleCount = idxDist.SampleCount;
            if (stringsSampled == 0) stringsSampled = sampleCount;
            progress?.Report(new(totalStrings, "string length distribution from index", $"{sampleCount:N0} samples from index metadata"));
        }
        else if (sampleCount == 0 && heapIndex?.StringDedupIndex is not null)
        {
            // Lightweight, index-only estimation: derive char-length estimates from TotalSize/Count
            var estLengths = new List<int>(capacity: 1024);
            var estLengthBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["0-15"] = 0,
                ["16-31"] = 0,
                ["32-63"] = 0,
                ["64-127"] = 0,
                ["128-255"] = 0,
                ["256-511"] = 0,
                ["512-1023"] = 0,
                ["1024-4095"] = 0,
                ["4096-16383"] = 0,
                ["16384-65535"] = 0,
                ["65536+"] = 0
            };
            var estFreq = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["1"] = 0,
                ["2"] = 0,
                ["3-10"] = 0,
                ["11-100"] = 0,
                ["101-1000"] = 0,
                ["1001+"] = 0
            };

            int taken = 0;
            foreach (var e in heapIndex.StringDedupIndex.Values)
            {
                if (taken >= 100_000) break;
                if (e.Count <= 0) continue;
                // average size per instance
                ulong avgSize = e.TotalSize / (ulong)e.Count;
                int estChars = avgSize > 26 ? (int)Math.Min((avgSize - 26) / 2, int.MaxValue) : 0;
                estLengths.Add(estChars);

                string key = estChars switch
                {
                    < 16 => "0-15",
                    < 32 => "16-31",
                    < 64 => "32-63",
                    < 128 => "64-127",
                    < 256 => "128-255",
                    < 512 => "256-511",
                    < 1024 => "512-1023",
                    < 4096 => "1024-4095",
                    < 16384 => "4096-16383",
                    < 65536 => "16384-65535",
                    _ => "65536+"
                };
                estLengthBuckets.TryGetValue(key, out int bc);
                estLengthBuckets[key] = bc + 1;

                int c = e.Count;
                if (c <= 1) estFreq["1"]++;
                else if (c == 2) estFreq["2"]++;
                else if (c <= 10) estFreq["3-10"]++;
                else if (c <= 100) estFreq["11-100"]++;
                else if (c <= 1000) estFreq["101-1000"]++;
                else estFreq["1001+"]++;

                taken++;
            }

            if (estLengths.Count > 0)
            {
                estLengths.Sort();
                sampleCount = estLengths.Count;
                percentiles = new Dictionary<string, double>
                {
                    ["p50"] = estLengths[(int)Math.Floor((sampleCount - 1) * 0.50)],
                    ["p75"] = estLengths[(int)Math.Floor((sampleCount - 1) * 0.75)],
                    ["p90"] = estLengths[(int)Math.Floor((sampleCount - 1) * 0.90)],
                    ["p95"] = estLengths[(int)Math.Floor((sampleCount - 1) * 0.95)]
                };
                lengthBuckets = estLengthBuckets;
                freqBuckets = estFreq;
                stringsSampled = Math.Max(stringsSampled, heapIndex.StringDedupIndex.Count);
                progress?.Report(new(totalStrings, "string length distribution estimated from dedup-index", $"{sampleCount:N0} estimated samples"));
            }
        }
        else
        {
            // Compute percentiles from lengthSamples (if any)
            if (sampleCount > 0)
            {
                lengthSamples.Sort();
                double p50 = lengthSamples[(int)Math.Floor((sampleCount - 1) * 0.50)];
                double p75 = lengthSamples[(int)Math.Floor((sampleCount - 1) * 0.75)];
                double p90 = lengthSamples[(int)Math.Floor((sampleCount - 1) * 0.90)];
                double p95 = lengthSamples[(int)Math.Floor((sampleCount - 1) * 0.95)];
                percentiles = new Dictionary<string, double>
                {
                    ["p50"] = p50,
                    ["p75"] = p75,
                    ["p90"] = p90,
                    ["p95"] = p95
                };
            }
        }

        double duplicationRatio = (!dedupSkipped && totalStrings > 0)
            ? (totalStrings - sampledUniquePatterns) / (double)totalStrings
            : 0.0;
        double pctOfManagedHeap = totalManagedBytes > 0
            ? totalStringMemory * 100.0 / totalManagedBytes
            : 0.0;

        double samplingCoverage = 0.0;
        if (totalStrings > 0)
            samplingCoverage = runDedup ? (stringsSampled / (double)totalStrings) : 0.0;

        // Map dominant method-tables to type names for reporting (top 10)
        IReadOnlyList<NameCountEntry>? topDuplicateTypes = null;
        if (methodTableDupCounts.Count > 0)
        {
            var top = new List<NameCountEntry>(capacity: Math.Min(10, methodTableDupCounts.Count));
            foreach (var kv in methodTableDupCounts.OrderByDescending(kv => kv.Value).Take(10))
            {
                string typeName = heap.GetTypeByMethodTable(kv.Key)?.Name ?? $"0x{kv.Key:X}";
                top.Add(new NameCountEntry(typeName, kv.Value));
            }
            topDuplicateTypes = top;
        }
        // Build raw exports when requested
        IReadOnlyList<DumpDetective.Core.Models.ReportArtifact>? rawExports = null;
        if (stringOptions.ProduceRawExports)
        {
            try
            {
                // JSON export: top duplicates by waste
                var exportObj = new
                {
                    TotalStrings = totalStrings,
                    TotalStringMemoryBytes = totalStringMemory,
                    SampledUniquePatterns = sampledUniquePatterns,
                    DuplicatePatternCount = duplicatePatternCount,
                    DuplicateWastedBytes = duplicateWastedBytes,
                    TopByWaste = topByWaste.Select(d => new { d.Preview, d.Count, d.WastedBytes, SampleAddresses = d.SampleAddresses, d.DominantMethodTable }),
                    TopByCount = topByCount.Select(d => new { d.Preview, d.Count, d.WastedBytes, SampleAddresses = d.SampleAddresses, d.DominantMethodTable })
                };
                string json = System.Text.Json.JsonSerializer.Serialize(exportObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                // CSV export: simple rows for topByWaste
                var sw = new System.Text.StringBuilder();
                sw.AppendLine("Preview,Count,WastedBytes,SampleAddresses,DominantMethodTable");
                foreach (var d in topByWaste)
                {
                    string samples = d.SampleAddresses is null ? "" : string.Join('|', d.SampleAddresses);
                    sw.Append('"').Append(d.Preview.Replace("\"", "\"\"")).Append('"').Append(',')
                      .Append(d.Count).Append(',').Append(d.WastedBytes).Append(',')
                      .Append('"').Append(samples).Append('"').Append(',')
                      .Append(d.DominantMethodTable).AppendLine();
                }

                var artifacts = new List<DumpDetective.Core.Models.ReportArtifact>(capacity: 3)
                {
                    new DumpDetective.Core.Models.ReportArtifact("String Analysis", "string-duplicates.json", json, "application/json"),
                    new DumpDetective.Core.Models.ReportArtifact("String Analysis", "string-duplicates.csv", sw.ToString(), "text/csv")
                };

                // Produce NDJSON gzipped export by streaming directly to a temp file (no base64)
                string tmp = Path.Combine(Path.GetTempPath(), $"dumpdetective-string-duplicates-{Guid.NewGuid():N}.ndjson.gz");
                try
                {
                    using (var fs = File.Create(tmp))
                    using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: false))
                    {
                        var jsOpts = new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
                        foreach (var d in topDuplicates)
                        {
                            // Tweak NDJSON contents: include preview, count, wasted, totalSize, avgSize, sampling, fingerprint hex, dominant type
                            var lineObj = new
                            {
                                preview = d.Preview,
                                count = d.Count,
                                wastedBytes = d.WastedBytes,
                                totalSize = d.TotalSize > 0 ? d.TotalSize : (ulong?)null,
                                avgSize = d.AvgSize > 0 ? d.AvgSize : (int?)null,
                                samplingSource = d.SamplingSource,
                                fingerprint = d.FingerprintHash.HasValue ? $"0x{d.FingerprintHash.Value:X16}" : null,
                                dominantMethodTable = d.DominantMethodTable != 0 ? $"0x{d.DominantMethodTable:X}" : null,
                                dominantType = d.DominantType,
                                sampleAddresses = d.SampleAddresses
                            };
                            System.Text.Json.JsonSerializer.Serialize(gz, lineObj, jsOpts);
                            gz.WriteByte((byte)'\n');
                        }
                    }

                    // Add artifact referencing the temp file path (WriteOutputStage will move it into artifacts dir)
                    artifacts.Add(new DumpDetective.Core.Models.ReportArtifact("String Analysis", "string-duplicates.ndjson.gz", null, "application/gzip", tmp));
                }
                catch
                {
                    // Cleanup on error
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }

                rawExports = artifacts;
            }
            catch
            {
                rawExports = null; // swallow export errors — reporting should not fail analysis
            }
        }

        totalStopwatch.Stop();

        // choose dedup source label when dedup was run
        if (runDedup)
        {
            if (heapIndex?.StringDedupIndex is not null && heapIndex.StringDedupIndex.Count > 0) dedupSource = "Prebuilt";
            else if (typeAggregates is not null) dedupSource = "IndexScan";
            else dedupSource = "HeapScan";
        }

        var distribution = new DistributionSummary(
            Percentiles: percentiles,
            LengthBuckets: lengthBuckets,
            FrequencyBuckets: freqBuckets,
            SampleCount: sampleCount);

        // Cap VeryLongStrings to top 1000 by size to prevent unbounded growth
        const int maxVeryLongStringsToKeep = 1000;
        if (veryLongStrings.Count > maxVeryLongStringsToKeep)
        {
            veryLongStrings.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            veryLongStrings.RemoveRange(maxVeryLongStringsToKeep, veryLongStrings.Count - maxVeryLongStringsToKeep);
        }

        // P1-3: top object types owning string fields.
        IReadOnlyList<(string TypeName, ulong TotalBytes)>? topStringOwnerTypes = null;
        if (stringMts.Count > 0)
        {
            try
            {
                Dictionary<ulong, ulong>? stringOwnerTypeBytes;

                if (typeAggregates is not null && _indexScanOwnerTypesActive)
                {
                    // Index available: the scan already happened as this analyzer's
                    // IHeapIndexScanParticipant.OnHeapEntry during the shared dispatcher pass
                    // (disk-backed, no live heap walk) — just read the accumulated result back.
                    stringOwnerTypeBytes = _indexScanStringOwnerTypeBytes;
                }
                else
                {
                    // No-index fallback: single live heap pass (rare path — small dumps / no cache).
                    stringOwnerTypeBytes = new Dictionary<ulong, ulong>(capacity: MaxStringOwnerTypesToTrack);
                    var fieldCache = new FieldLayoutCache();
                    ScanForStringOwnerTypesFallback(heap, stringMts, stringOwnerTypeBytes, fieldCache, progress, MaxStringOwnerTypesToTrack);
                }

                if (stringOwnerTypeBytes is not null && stringOwnerTypeBytes.Count > 0)
                {
                    var topOwners = new List<(string, ulong)>(capacity: Math.Min(10, stringOwnerTypeBytes.Count));
                    foreach (var kv in stringOwnerTypeBytes.OrderByDescending(kv => kv.Value).Take(10))
                    {
                        string typeName = heap.GetTypeByMethodTable(kv.Key)?.Name ?? $"0x{kv.Key:X}";
                        topOwners.Add((typeName, kv.Value));
                    }
                    topStringOwnerTypes = topOwners;
                }
            }
            catch
            {
                topStringOwnerTypes = null; // swallow errors gracefully
            }
        }

        return new StringDomainResult(
            TotalStrings: totalStrings,
            TotalStringMemoryBytes: totalStringMemory,
            SampledUniquePatterns: sampledUniquePatterns,
            DuplicatePatternCount: duplicatePatternCount,
            DuplicateWastedBytes: duplicateWastedBytes,
            DuplicationRatio: duplicationRatio,
            PctOfManagedHeap: pctOfManagedHeap,
            TopDuplicates: topDuplicates,
            VeryLongStrings: veryLongStrings,
            LohStringBytes: lohStringBytes,
            InternedStringCount: internedStringCount,
            InternedStringBytes: internedStringBytes,
            Gen2StringCount: gen2StringCount,
            Gen2StringBytes: gen2StringBytes,
            DeduplicationSkipped: dedupSkipped,
            StringsSampled: runDedup ? stringsSampled : 0,
            SamplingCoverage: samplingCoverage,
            // new metadata
            SamplingMode: stringOptions.SamplingMode.ToString(),
            DeduplicationMode: stringOptions.DeduplicationMode.ToString(),
            DeduplicationThreshold: stringOptions.DeduplicationStringCountThreshold,
            MaxStringsToDedup: ComputeEffectiveCaps(stringOptions, stringOptions.MaxStringsToDedup, stringOptions.MaxUniqueStringTracking).MaxStringsToDedup,
            DedupSource: dedupSource,
            AnalysisDurationMs: totalStopwatch.ElapsedMilliseconds,
            DedupSkipReason: dedupSkipped ? $"Dedup skipped: threshold={stringOptions.DeduplicationStringCountThreshold}" : null,
            TopDuplicateTypes: topDuplicateTypes,
            TopStringOwnerTypes: topStringOwnerTypes,
            Distribution: distribution,
            PreviewMaxLength: stringOptions.PreviewMaxLength,
            Artifacts: rawExports);
    }

    // Internal helper used by the analyzer and unit tests to compute effective numeric caps
    // from the semantic `StringSamplingMode` hint and configured base caps.
    internal static (int MaxStringsToDedup, int MaxUniqueStringTracking) ComputeEffectiveCaps(
        DumpDetective.Core.Options.StringAnalysisOptions options,
        int baseMaxToDedup,
        int baseMaxUnique)
    {
        int maxToDedup = baseMaxToDedup;
        int maxUnique = baseMaxUnique;

        switch (options.SamplingMode)
        {
            case DumpDetective.Core.Options.StringSamplingMode.Aggressive:
                maxToDedup = Math.Max(1_000, (int)(maxToDedup * 0.25));
                maxUnique = Math.Max(10_000, (int)(maxUnique * 0.25));
                break;
            case DumpDetective.Core.Options.StringSamplingMode.Full:
                maxToDedup = Math.Min(int.MaxValue / 2, (int)(maxToDedup * 2));
                maxUnique = Math.Min(int.MaxValue / 2, (int)(maxUnique * 2));
                break;
            case DumpDetective.Core.Options.StringSamplingMode.Moderate:
            default:
                break;
        }

        return (maxToDedup, maxUnique);
    }

    /// <summary>
    /// Read a string at <paramref name="address"/>, create a fingerprint and
    /// aggregate it into <paramref name="stringStats"/>. Uses <see cref="ClrObject.AsString(int)"/>
    /// with a maximum length to bound allocations.
    /// </summary>
    private static void FingerprintAddress(
        ClrHeap heap,
        ulong address,
        ulong size,
        StringAnalysisOptions stringOptions,
        Dictionary<StringFingerprint, StringLeakInfo> stringStats,
        int maxUniqueTracking,
        Dictionary<ulong, int> methodTableDupCounts,
        List<int> lengthSamples,
        Dictionary<string, int> lengthBuckets,
        string samplingSource = "HeapScan")
    {
        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid) return;

        // Pass maxLength so ClrMD never allocates more than we're willing to process.
        string? value = obj.AsString(maxLength: stringOptions.MaxDuplicateStringLength - 1);
        if (value is null || value.Length == 0) return;
        if (value.Length < stringOptions.MinDuplicateCharLength) return;

        var fingerprint = CreateFingerprint(value);

        if (!stringStats.ContainsKey(fingerprint) && stringStats.Count >= maxUniqueTracking) return;

        ref StringLeakInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(
            stringStats, fingerprint, out bool existed);

        if (!existed)
        {
            info.Preview = CreatePreview(value, stringOptions.PreviewMaxLength);
            info.SampleAddresses = new ulong[] { address };
            info.DominantMethodTable = obj.Type?.MethodTable ?? 0;
            info.FingerprintHash = fingerprint.Hash;
            info.SamplingSource = samplingSource;
        }
        else
        {
            if (info.SampleAddresses is null) info.SampleAddresses = new ulong[] { address };
            else if (info.SampleAddresses.Length == 1 && info.SampleAddresses[0] != address)
                info.SampleAddresses = new ulong[] { info.SampleAddresses[0], address };
            if (info.DominantMethodTable == 0 && obj.Type is not null) info.DominantMethodTable = obj.Type.MethodTable;
            // Ensure sampling source is set for existing entries when missing
            if (string.IsNullOrEmpty(info.SamplingSource)) info.SamplingSource = samplingSource;
        }

        // record length samples and buckets (bounded)
        int charLen = value.Length;
        if (lengthSamples is not null && lengthSamples.Count < 100_000) lengthSamples.Add(charLen);
        if (lengthBuckets is not null)
        {
            string key = charLen switch
            {
                < 16 => "0-15",
                < 32 => "16-31",
                < 64 => "32-63",
                < 128 => "64-127",
                < 256 => "128-255",
                < 512 => "256-511",
                < 1024 => "512-1023",
                < 4096 => "1024-4095",
                < 16384 => "4096-16383",
                < 65536 => "16384-65535",
                _ => "65536+"
            };
            lengthBuckets.TryGetValue(key, out int bc);
            lengthBuckets[key] = bc + 1;
        }

        info.Count++;
        info.TotalSize += size;
        if (info.DominantMethodTable != 0)
        {
            methodTableDupCounts.TryGetValue(info.DominantMethodTable, out int c);
            methodTableDupCounts[info.DominantMethodTable] = c + 1;
        }
    }
    /// Uses a small cache of known string method tables to avoid repeated lookups.
    /// </summary>
    private static bool IsStringMt(ClrHeap heap, ulong mt, HashSet<ulong> stringMts)
    {
        if (mt == 0) return false;
        if (stringMts.Contains(mt)) return true;

        // Fallback for the no-index path: resolve type name.
        ClrType? type = heap.GetTypeByMethodTable(mt);
        if (type is not null && string.Equals(type.Name, "System.String", StringComparison.Ordinal))
        {
            stringMts.Add(mt);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Heuristic to decide whether the raw object size likely corresponds to
    /// a string whose character length is within the configured maximum.
    /// Uses the same approximate formula elsewhere in the code: chars ~= (size-26)/2.
    /// </summary>
    private static bool IsStringSizeInBounds(ulong size, StringAnalysisOptions stringOptions)
    {
        if (stringOptions.MaxDuplicateStringLength <= 0) return true;
        if (size <= 26) return true; // empty/very small strings
        try
        {
            ulong estChars = (size - 26) / 2;
            return estChars <= (ulong)stringOptions.MaxDuplicateStringLength;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Return true when the given <paramref name="segment"/> lies inside any
    /// of the FOH ranges produced by <see cref="BuildFohSegments"/>.
    /// </summary>
    private static bool IsSegmentInFoh(ClrSegment segment, List<(ulong Start, ulong End)> fohSegments)
    {
        ulong s = segment.Start;
        for (int i = 0; i < fohSegments.Count; i++)
            if (s >= fohSegments[i].Start && s < fohSegments[i].End) return true;
        return false;
    }

    /// <summary>
    /// Compute number of unique string patterns observed.
    /// </summary>
    private static int ComputeUniqueCount(Dictionary<StringFingerprint, StringLeakInfo> stringStats)
    {
        return stringStats.Count;
    }

    /// <summary>Format a byte count as a human-readable string.</summary>
    private static string FormatBytes(ulong bytes) =>
        bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F1} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes} B";


    /// <summary>
    /// Build the list of FOH segment ranges (Start, End) from heap segments.
    /// Uses <c>ClrSegment.Kind</c> via reflection to detect Frozen segments
    /// (same approach as <see cref="HeapTopologyAnalyzer"/>).
    /// Only FOH segments are returned, so per-string range checks are O(foh_segments)
    /// instead of O(total_segments).
    /// </summary>
    private static List<(ulong Start, ulong End)> BuildFohSegments(ClrHeap heap)
    {
        var list = new List<(ulong, ulong)>(capacity: 4);
        foreach (ClrSegment segment in heap.Segments)
        {
            if (SegmentKindMapper.Map(segment) == HeapSegmentKind.Frozen)
                list.Add((segment.Start, segment.End));
        }
        return list;
    }

    /// <summary>Return true when the address lies inside a FOH segment.</summary>
    private static bool IsInFoh(ulong address, List<(ulong Start, ulong End)> fohSegments)
    {
        for (int i = 0; i < fohSegments.Count; i++)
        {
            (ulong start, ulong end) = fohSegments[i];
            if (address >= start && address < end)
                return true;
        }
        return false;
    }

    /// <summary>Return total managed bytes using the heap index when available.</summary>
    private static ulong GetTotalManagedBytes(AnalysisContext context)
    {
        if (context.Cache is HeapAnalysisCache concreteCache &&
            concreteCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            ulong total = 0;
            foreach (var entry in heapIndex.TypeAggregates.Values)
                total += entry.TotalSize;
            return total;
        }

        // Fallback: sum segment committed memory.
        ulong totalBytes = 0;
        foreach (ClrSegment segment in context.Heap.Segments)
            totalBytes += (ulong)(segment.End - segment.Start);
        return totalBytes;
    }

    /// <summary>Drain a priority queue into descending wasted bytes snapshots.</summary>
    private static IReadOnlyList<DuplicateStringSnapshot> DrainToDescendingWaste(
        PriorityQueue<StringLeakInfo, ulong> pq,
        IReadOnlyDictionary<ulong, string?> mtToName)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
            int avg = info.Count > 0 ? (int)Math.Min(info.TotalSize / (ulong)info.Count, int.MaxValue) : 0;
            string? dominantType = null;
            if (info.DominantMethodTable != 0 && mtToName is not null && mtToName.TryGetValue(info.DominantMethodTable, out var n))
                dominantType = n;
            list.Add(new DuplicateStringSnapshot(
                info.Preview ?? string.Empty,
                info.Count,
                wasted,
                info.SampleAddresses,
                info.DominantMethodTable,
                DominantType: dominantType,
                FingerprintHash: info.FingerprintHash == 0 ? null : info.FingerprintHash,
                TotalSize: info.TotalSize,
                AvgSize: avg,
                SamplingSource: info.SamplingSource));
        }
        list.Reverse();
        return list;
    }

    /// <summary>Drain a priority queue into descending count snapshots.</summary>
    private static IReadOnlyList<DuplicateStringSnapshot> DrainToDescendingCount(
        PriorityQueue<StringLeakInfo, int> pq,
        IReadOnlyDictionary<ulong, string?> mtToName)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize * (ulong)(info.Count - 1) / (ulong)info.Count;
            int avg = info.Count > 0 ? (int)Math.Min(info.TotalSize / (ulong)info.Count, int.MaxValue) : 0;
            string? dominantType = null;
            if (info.DominantMethodTable != 0 && mtToName is not null && mtToName.TryGetValue(info.DominantMethodTable, out var n))
                dominantType = n;
            list.Add(new DuplicateStringSnapshot(
                info.Preview ?? string.Empty,
                info.Count,
                wasted,
                info.SampleAddresses,
                info.DominantMethodTable,
                DominantType: dominantType,
                FingerprintHash: info.FingerprintHash == 0 ? null : info.FingerprintHash,
                TotalSize: info.TotalSize,
                AvgSize: avg,
                SamplingSource: info.SamplingSource));
        }
        list.Reverse();
        return list;
    }

    private static IReadOnlyList<DuplicateStringSnapshot> MergeTopDuplicates(
        IReadOnlyList<DuplicateStringSnapshot> byWaste,
        IReadOnlyList<DuplicateStringSnapshot> byCount)
    {
        if (byWaste.Count == 0 && byCount.Count == 0)
            return Array.Empty<DuplicateStringSnapshot>();

        var merged = new Dictionary<string, DuplicateStringSnapshot>(StringComparer.Ordinal);

        static string KeyFor(DuplicateStringSnapshot s)
            => s.FingerprintHash is ulong h ? $"h:{h:X16}" : $"p:{s.Preview}";

        void MergeIn(IReadOnlyList<DuplicateStringSnapshot> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                DuplicateStringSnapshot current = source[i];
                string key = KeyFor(current);
                if (!merged.TryGetValue(key, out DuplicateStringSnapshot? existing) || existing is null)
                {
                    merged[key] = current;
                    continue;
                }

                // Prefer the richer/bigger snapshot when the same duplicate appears in both rankings.
                if (current.WastedBytes > existing.WastedBytes ||
                    (current.WastedBytes == existing.WastedBytes && current.Count > existing.Count))
                {
                    merged[key] = current;
                }
            }
        }

        MergeIn(byWaste);
        MergeIn(byCount);

        return merged.Values
            .OrderByDescending(static d => d.WastedBytes)
            .ThenByDescending(static d => d.Count)
            .ThenByDescending(static d => d.TotalSize)
            .ToArray();
    }

    /// <summary>Create a compact fingerprint for a string value.</summary>
    private static StringFingerprint CreateFingerprint(string value)
    {
        // XxHash64 over raw UTF-16 bytes — SIMD-accelerated, faster than FNV-1a character loop.
        ulong hash = XxHash64.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()));
        return new StringFingerprint(hash, value.Length, value[0], value[^1]);
    }

    /// <summary>Create a display-safe preview for a string value.</summary>
    private static string CreatePreview(string value, int maxLength)
    {
        int cut = Math.Max(8, Math.Min(maxLength, value.Length));
        string preview = value.Length > cut ? value[..cut] + "..." : value;
        return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);

    /// <summary>
    /// No-index fallback: live heap.EnumerateObjects() walk for string-field ownership.
    /// Only used when no disk-backed heap index is available (small dumps / uncached runs) —
    /// the normal path piggybacks on the shared <see cref="IHeapIndexScanParticipant"/> pass
    /// via <see cref="AccumulateStringOwnerType"/> instead.
    /// </summary>
    private static void ScanForStringOwnerTypesFallback(
        ClrHeap heap,
        HashSet<ulong> stringMts,
        Dictionary<ulong, ulong> stringOwnerTypeBytes,
        FieldLayoutCache fieldCache,
        IProgress<AnalyzerProgressReport>? progress,
        int maxTypesToTrack)
    {
        // Cache: MethodTable -> string field indices (built on-demand per type)
        var stringFieldsByType = new Dictionary<ulong, int[]>(capacity: 1000);
        var typesWithoutStringFields = new HashSet<ulong>(capacity: 1000); // Negative cache

        int typesTracked = 0;
        var sc = new ObjectScanCounter("scanning for string-owning types", progress);

        foreach (var obj in heap.EnumerateObjects())
        {
            sc.Tick();
            if (!obj.IsValid || obj.Type == null) continue;

            var mt = obj.Type.MethodTable;

            // Check negative cache first (common case: type has no string fields)
            if (typesWithoutStringFields.Contains(mt))
                continue;

            // Check positive cache
            if (!stringFieldsByType.TryGetValue(mt, out var stringFieldIndices))
            {
                // Build cache entry for this type (first time we see it)
                var fields = fieldCache.GetFields(obj.Type);
                var stringIndices = new List<int>(capacity: 4);

                for (int i = 0; i < fields.Length; i++)
                {
                    try
                    {
                        var fieldType = fields[i].Type;
                        if (fieldType != null && stringMts.Contains(fieldType.MethodTable))
                        {
                            stringIndices.Add(i);
                        }
                    }
                    catch
                    {
                        // Skip malformed fields
                        continue;
                    }
                }

                if (stringIndices.Count > 0)
                {
                    stringFieldIndices = stringIndices.ToArray();
                    stringFieldsByType[mt] = stringFieldIndices;
                }
                else
                {
                    // Negative cache: this type has no string fields
                    typesWithoutStringFields.Add(mt);
                    continue;
                }
            }

            // Process string fields for this object
            var objFields = fieldCache.GetFields(obj.Type);
            foreach (int fieldIndex in stringFieldIndices)
            {
                try
                {
                    if (fieldIndex >= objFields.Length) continue;
                    var field = objFields[fieldIndex];

                    var stringRef = field.ReadObject(obj, interior: false);
                    if (!stringRef.IsValid) continue;

                    var ownerMt = obj.Type.MethodTable;
                    if (!stringOwnerTypeBytes.TryGetValue(ownerMt, out ulong existing))
                    {
                        if (stringOwnerTypeBytes.Count >= maxTypesToTrack) continue;
                        typesTracked++;
                    }
                    stringOwnerTypeBytes[ownerMt] = existing + stringRef.Size;
                }
                catch
                {
                    // Safely skip malformed fields
                    continue;
                }
            }
        }

        sc.Complete();
    }

    public void Dispose() { }
}
