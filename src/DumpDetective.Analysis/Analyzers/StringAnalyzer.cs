using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Analyze managed string usage: counts, sizes, LOH/FOH stats and duplicate patterns.
/// Prefers pre-built string dedup index when available to avoid random dump I/O.
/// </summary>
internal sealed class StringAnalyzer : IAnalyzer
{
    // File-level constants removed. Use StringAnalysisOptions for configurable thresholds.

    /// <inheritdoc/>
    public string Name => "String Analysis";

    /// <inheritdoc/>
    public string Category => "Memory";

    /// <summary>
    /// Analyze the provided <see cref="AnalysisContext"/> and return a <see cref="AnalyzerDomainResult"/>.
    /// This method is the I/O entry point and delegates to internal helpers.
    /// </summary>
    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MemoryLeakOptions options = context.GetOption<MemoryLeakOptions>();
        StringAnalysisOptions stringOptions = context.GetOption<StringAnalysisOptions>();
        ulong totalManagedBytes = GetTotalManagedBytes(context);
        List<(ulong Start, ulong End)> fohSegments = BuildFohSegments(context.Heap);

        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, stringOptions, totalManagedBytes, fohSegments, context.Progress).Stamp(this));
    }

    /// <summary>
    /// Core analysis implementation. Separated for easier unit testing and to keep
    /// the public entry point small.
    /// </summary>
    private static AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        MemoryLeakOptions options,
        StringAnalysisOptions stringOptions,
        ulong totalManagedBytes,
        List<(ulong Start, ulong End)> fohSegments,
        IProgress<AnalyzerProgressReport>? progress)
    {
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
        int gen2StringCount = 0;
        ulong gen2StringBytes = 0;
        var veryLongStrings = new List<LongStringEntry>(capacity: 16);

        if (typeAggregates is not null && stringMts.Count > 0)
        {
            foreach (ulong mt in stringMts)
            {
                if (!typeAggregates.TryGetValue(mt, out TypeAggregateIndexEntry entry)) continue;
                totalStrings     += (int)Math.Min(entry.Count, int.MaxValue);
                totalStringMemory += entry.TotalSize;
                lohStringBytes   += entry.LohSize;
                gen2StringCount  += entry.Gen2Count;
                if (entry.Count > 0)
                    gen2StringBytes += (ulong)entry.Gen2Count * (entry.TotalSize / (ulong)entry.Count);
            }
            progress?.Report(new(totalStrings, "string stats from index", $"{totalStrings:N0} strings, {FormatBytes(totalStringMemory)} total"));
        }

        // ── Interned strings: scan only FOH segments (tiny — typically 1–2 segments) ────
        int internedStringCount = 0;
        ulong internedStringBytes = 0;
        if (fohSegments.Count > 0)
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
        var methodTableDupCounts = new Dictionary<ulong,int>(capacity: 64);
        bool dedupSkipped = false;

        bool runDedup = stringOptions.EnableDeduplication
            && totalStrings <= stringOptions.DeduplicationStringCountThreshold;

        if (!runDedup && totalStrings > 0)
        {
            dedupSkipped = true;
            progress?.Report(new(totalStrings, "string dedup skipped",
                $"{totalStrings:N0} strings exceed threshold ({stringOptions.DeduplicationStringCountThreshold:N0}). Raise DeduplicationStringCountThreshold or set EnableDeduplication=true explicitly."));
        }

        int stringsSampled = 0;
        if (runDedup)
        {
            int maxToDedup = stringOptions.MaxStringsToDedup;
            int maxUnique  = stringOptions.MaxUniqueStringTracking;

            // ── Fast path: use pre-built dedup index from heap scan (zero dump I/O) ──────
            // During index build, AsString() is called while object pages are hot from
            // type resolution reads. Using that result here costs nothing extra.
            if (heapIndex?.StringDedupIndex is { Count: > 0 } prebuilt)
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
                    }
                    entry.Count     += kvp.Value.Count;
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
                // Index available but no pre-built dedup (e.g. disk-backed with cached index).
                // Fall back to capped AsString() scan.
                int stringsRead = 0;
                var sc = new ObjectScanCounter("string dedup (index scan)", progress);
                foreach (var (address, mt, size) in cache!.EnumerateIndexedEntriesAsTuples())
                {
                    sc.Tick();
                    if (!IsStringMt(heap, mt, stringMts)) continue;
                    if (size >= (ulong)stringOptions.VeryLongStringThresholdBytes)
                    {
                        int ecl = (int)Math.Min((size - 26) / 2, int.MaxValue);
                        veryLongStrings.Add(new LongStringEntry(address, ecl, size));
                    }
                    if (stringsRead >= maxToDedup) continue;
                    if (!IsStringSizeInBounds(size, options)) continue;
                    stringsRead++;
                    FingerprintAddress(heap, address, size, options, stringOptions, stringStats, maxUnique, methodTableDupCounts);
                }
                sc.Complete();
                progress?.Report(new(totalStrings, "string dedup complete",
                    $"{stringsRead:N0} strings sampled from {totalStrings:N0} total"));
                stringsSampled = stringsRead;
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
                        veryLongStrings.Add(new LongStringEntry(obj.Address, ecl, obj.Size));
                    }
                    if (fohSegments.Count > 0 && IsInFoh(obj.Address, fohSegments))
                    { internedStringCount++; internedStringBytes += obj.Size; continue; }

                    if (stringsRead < maxToDedup && IsStringSizeInBounds(obj.Size, options))
                    {
                        stringsRead++;
                        FingerprintAddress(heap, obj.Address, obj.Size, options, stringOptions, stringStats, maxUnique, methodTableDupCounts);
                    }
                }
                sc.Complete();
                stringsSampled = stringsRead;
            }
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
                    veryLongStrings.Add(new LongStringEntry(obj.Address, ecl, obj.Size));
                if (fohSegments.Count > 0 && IsInFoh(obj.Address, fohSegments))
                { internedStringCount++; internedStringBytes += obj.Size; }
                stringMts.Add(obj.Type.MethodTable);
            }
            sc.Complete();
        }

        // ── Aggregate dedup results ──────────────────────────────────────────────────────
        int uniqueStrings = dedupSkipped ? 0 : ComputeUniqueCount(stringStats);
        int duplicatePatternCount = 0;
        ulong duplicateWastedBytes = 0;

        var byWasteHeap = new PriorityQueue<StringLeakInfo, ulong>(stringOptions.TopDuplicatesToShow + 1);
        var byCountHeap = new PriorityQueue<StringLeakInfo, int>(stringOptions.TopDuplicatesToShow + 1);

        int minCount = options.MinDuplicateStringCount;
        foreach (StringLeakInfo info in stringStats.Values)
        {
            if (info.Count <= minCount) continue;
            duplicatePatternCount++;
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            duplicateWastedBytes += wasted;
            byWasteHeap.Enqueue(info, info.TotalSize);
            if (byWasteHeap.Count > stringOptions.TopDuplicatesToShow) byWasteHeap.Dequeue();
            byCountHeap.Enqueue(info, info.Count);
            if (byCountHeap.Count > stringOptions.TopDuplicatesToShow) byCountHeap.Dequeue();
        }

        IReadOnlyList<DuplicateStringSnapshot> topByWaste = DrainToDescendingWaste(byWasteHeap);
        IReadOnlyList<DuplicateStringSnapshot> topByCount = DrainToDescendingCount(byCountHeap);

        double duplicationRatio = (!dedupSkipped && totalStrings > 0)
            ? (totalStrings - uniqueStrings) / (double)totalStrings
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
        return new StringDomainResult(
            TotalStrings: totalStrings,
            TotalStringMemoryBytes: totalStringMemory,
            UniqueStrings: uniqueStrings,
            DuplicatePatternCount: duplicatePatternCount,
            DuplicateWastedBytes: duplicateWastedBytes,
            DuplicationRatio: duplicationRatio,
            PctOfManagedHeap: pctOfManagedHeap,
            TopDuplicatesByWaste: topByWaste,
            TopDuplicatesByCount: topByCount,
            VeryLongStrings: veryLongStrings,
            LohStringBytes: lohStringBytes,
            InternedStringCount: internedStringCount,
            InternedStringBytes: internedStringBytes,
            Gen2StringCount: gen2StringCount,
            Gen2StringBytes: gen2StringBytes,
            DeduplicationSkipped: dedupSkipped,
            StringsSampled: runDedup ? stringsSampled : 0,
            SamplingCoverage: samplingCoverage,
            TopDuplicateTypes: topDuplicateTypes,
            PreviewMaxLength: stringOptions.PreviewMaxLength);
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
        MemoryLeakOptions options,
        StringAnalysisOptions stringOptions,
        Dictionary<StringFingerprint, StringLeakInfo> stringStats,
        int maxUniqueTracking,
        Dictionary<ulong,int> methodTableDupCounts)
    {
        ClrObject obj = heap.GetObject(address);
        if (!obj.IsValid) return;

        // Pass maxLength so ClrMD never allocates more than we're willing to process.
        string? value = obj.AsString(maxLength: options.MaxDuplicateStringLength - 1);
        if (value is null || value.Length == 0) return;

        var fingerprint = CreateFingerprint(value);

        if (!stringStats.ContainsKey(fingerprint) && stringStats.Count >= maxUniqueTracking) return;

        ref StringLeakInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(
            stringStats, fingerprint, out bool existed);
        if (!existed)
        {
            info.Preview = CreatePreview(value, stringOptions.PreviewMaxLength);
            info.SampleAddresses = new ulong[] { address };
            info.DominantMethodTable = obj.Type?.MethodTable ?? 0;
        }
        else
        {
            // capture up to two sample addresses
            if (info.SampleAddresses is null) info.SampleAddresses = new ulong[] { address };
            else if (info.SampleAddresses.Length == 1 && info.SampleAddresses[0] != address)
                info.SampleAddresses = new ulong[] { info.SampleAddresses[0], address };
            if (info.DominantMethodTable == 0 && obj.Type is not null) info.DominantMethodTable = obj.Type.MethodTable;
        }
        info.Count++;
        info.TotalSize += size;
        if (info.DominantMethodTable != 0)
        {
            methodTableDupCounts.TryGetValue(info.DominantMethodTable, out int c);
            methodTableDupCounts[info.DominantMethodTable] = c + 1;
        }
    }

    /// <summary>
    /// Returns true when the string's estimated char length is within dedup bounds.
    /// Strings outside these bounds are skipped to avoid noise (empty/huge strings).
    /// </summary>
    private static bool IsStringSizeInBounds(ulong size, MemoryLeakOptions options)
    {
        if (size < 28) return false; // too small to have meaningful content
        int estimatedCharLen = (int)Math.Min((size - 26) / 2, int.MaxValue);
        return estimatedCharLen > 0 && estimatedCharLen < options.MaxDuplicateStringLength;
    }

    /// <summary>
    /// Return true when <paramref name="mt"/> corresponds to System.String.
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
    /// (same approach as <see cref="SegmentAnalyzer"/>).
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
        PriorityQueue<StringLeakInfo, ulong> pq)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            list.Add(new DuplicateStringSnapshot(info.Preview ?? string.Empty, info.Count, wasted, info.SampleAddresses, info.DominantMethodTable));
        }
        list.Reverse();
        return list;
    }

    /// <summary>Drain a priority queue into descending count snapshots.</summary>
    private static IReadOnlyList<DuplicateStringSnapshot> DrainToDescendingCount(
        PriorityQueue<StringLeakInfo, int> pq)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            list.Add(new DuplicateStringSnapshot(info.Preview ?? string.Empty, info.Count, wasted, info.SampleAddresses, info.DominantMethodTable));
        }
        list.Reverse();
        return list;
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

    public void Dispose() { }
}
