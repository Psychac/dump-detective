using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.Analyzers;

internal sealed class StringAnalyzer : IAnalyzer
{
    private const int TopDuplicatesToShow = 20;
    private const int VeryLongStringThresholdBytes = 85_000;
    private const ulong LohThresholdBytes = 85_000;

    public string Name => "String Analysis";
    public string Category => "Memory";

    public ValueTask<AnalyzerDomainResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MemoryLeakOptions options = context.GetOption<MemoryLeakOptions>();
        ulong totalManagedBytes = GetTotalManagedBytes(context);
        List<(ulong Start, ulong End)> fohSegments = BuildFohSegments(context.Heap);

        return ValueTask.FromResult(Analyze(context.Heap, context.Cache, options, totalManagedBytes, fohSegments, context.Progress).Stamp(this));
    }

    private static AnalyzerDomainResult Analyze(
        ClrHeap heap,
        IHeapAnalysisCache? cache,
        MemoryLeakOptions options,
        ulong totalManagedBytes,
        List<(ulong Start, ulong End)> fohSegments,
        IProgress<AnalyzerProgressReport>? progress)
    {
        // Build string MT set from TypeAggregates flags (Phase 1 fast path).
        var stringMts = new HashSet<ulong>(capacity: 4);
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry>? typeAggregates = null;

        if (cache is HeapAnalysisCache concreteCache && concreteCache.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
        {
            typeAggregates = heapIndex.TypeAggregates;
            foreach (var kvp in heapIndex.TypeAggregates)
            {
                if ((kvp.Value.Flags & TypeAggregateFlags.IsStringType) != 0)
                    stringMts.Add(kvp.Key);
            }
        }

        var stringStats = new Dictionary<StringFingerprint, StringLeakInfo>(capacity: 1024);
        int totalStrings = 0;
        ulong totalStringMemory = 0;
        ulong lohStringBytes = 0;
        int internedStringCount = 0;
        ulong internedStringBytes = 0;
        var veryLongStrings = new List<LongStringEntry>(capacity: 16);

        // Enumerate via indexed tuples when available, fall back to raw heap scan.
        if (cache is HeapAnalysisCache cacheWithIndex && cacheWithIndex.TryGetHeapIndex(out _))
        {
            var scanCounter = new ObjectScanCounter("scanning string objects (indexed)", progress);
            foreach (var (address, mt, size) in cache!.EnumerateIndexedEntriesAsTuples())
            {
                scanCounter.Tick();
                if (!IsStringMt(heap, mt, stringMts))
                    continue;

                ProcessString(heap, address, size, options, stringStats, fohSegments,
                    ref totalStrings, ref totalStringMemory, ref lohStringBytes,
                    ref internedStringCount, ref internedStringBytes, veryLongStrings);
            }
            scanCounter.Complete();
        }
        else
        {
            var scanCounter = new ObjectScanCounter("scanning string objects", progress);
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                scanCounter.Tick();
                if (!obj.IsValid || obj.Type is null)
                    continue;

                if (!string.Equals(obj.Type.Name, "System.String", StringComparison.Ordinal))
                    continue;

                stringMts.Add(obj.Type.MethodTable);
                ProcessString(heap, obj.Address, obj.Size, options, stringStats, fohSegments,
                    ref totalStrings, ref totalStringMemory, ref lohStringBytes,
                    ref internedStringCount, ref internedStringBytes, veryLongStrings);
            }
            scanCounter.Complete();
        }

        // Compute Gen2 string count/bytes from TypeAggregates (zero heap re-scan).
        int gen2StringCount = 0;
        ulong gen2StringBytes = 0;
        if (typeAggregates is not null)
        {
            foreach (ulong mt in stringMts)
            {
                if (typeAggregates.TryGetValue(mt, out TypeAggregateIndexEntry entry))
                {
                    gen2StringCount += entry.Gen2Count;
                    // Estimate per-object average size from aggregate.
                    if (entry.Count > 0)
                        gen2StringBytes += (ulong)entry.Gen2Count * (entry.TotalSize / (ulong)entry.Count);
                }
            }
        }

        int uniqueStrings = stringStats.Count;
        int duplicatePatternCount = 0;
        ulong duplicateWastedBytes = 0;

        var byWasteHeap = new PriorityQueue<StringLeakInfo, ulong>(TopDuplicatesToShow + 1);
        var byCountHeap = new PriorityQueue<StringLeakInfo, int>(TopDuplicatesToShow + 1);

        int minCount = options.MinDuplicateStringCount;
        foreach (StringLeakInfo info in stringStats.Values)
        {
            if (info.Count <= minCount)
                continue;

            duplicatePatternCount++;
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            duplicateWastedBytes += wasted;

            byWasteHeap.Enqueue(info, info.TotalSize);
            if (byWasteHeap.Count > TopDuplicatesToShow)
                byWasteHeap.Dequeue();

            byCountHeap.Enqueue(info, info.Count);
            if (byCountHeap.Count > TopDuplicatesToShow)
                byCountHeap.Dequeue();
        }

        IReadOnlyList<DuplicateStringSnapshot> topByWaste = DrainToDescendingWaste(byWasteHeap);
        IReadOnlyList<DuplicateStringSnapshot> topByCount = DrainToDescendingCount(byCountHeap);

        double duplicationRatio = totalStrings > 0
            ? (totalStrings - uniqueStrings) / (double)totalStrings
            : 0.0;
        double pctOfManagedHeap = totalManagedBytes > 0
            ? totalStringMemory * 100.0 / totalManagedBytes
            : 0.0;

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
            Gen2StringBytes: gen2StringBytes);
    }

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

    private static void ProcessString(
        ClrHeap heap,
        ulong address,
        ulong size,
        MemoryLeakOptions options,
        Dictionary<StringFingerprint, StringLeakInfo> stringStats,
        List<(ulong Start, ulong End)> fohSegments,
        ref int totalStrings,
        ref ulong totalStringMemory,
        ref ulong lohStringBytes,
        ref int internedStringCount,
        ref ulong internedStringBytes,
        List<LongStringEntry> veryLongStrings)
    {
        if (address == 0) return;

        totalStrings++;
        totalStringMemory += size;

        if (size >= LohThresholdBytes)
            lohStringBytes += size;

        // OPT: Approximate char length from size before heap dereference.
        // .NET string layout: 8 (object header) + 8 (MT) + 4 (length field) + 2*N + 2 (null) ≈ 26 + 2N
        int estimatedCharLength = size > 26 ? (int)Math.Min((size - 26) / 2, int.MaxValue) : 0;

        if (size >= VeryLongStringThresholdBytes)
            veryLongStrings.Add(new LongStringEntry(address, estimatedCharLength, size));

        // Interned strings live in FOH (frozen object heap) segments.
        if (fohSegments.Count > 0 && IsInFoh(address, fohSegments))
        {
            internedStringCount++;
            internedStringBytes += size;
            return; // Don't fingerprint interned strings as duplicates.
        }

        // Skip very long strings for deduplication (already captured above).
        if (estimatedCharLength >= options.MaxDuplicateStringLength)
            return;

        ClrObject stringObject = heap.GetObject(address);
        if (!stringObject.IsValid)
            return;

        string? value = stringObject.AsString();
        if (value is null || value.Length == 0 || value.Length >= options.MaxDuplicateStringLength)
            return;

        var fingerprint = CreateFingerprint(value);
        ref StringLeakInfo info = ref CollectionsMarshal.GetValueRefOrAddDefault(
            stringStats, fingerprint, out bool existed);

        if (!existed)
            info.Preview = CreatePreview(value);

        info.Count++;
        info.TotalSize += size;
    }

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
            string? kindName = segment.GetType()
                .GetProperty("Kind", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                ?.GetValue(segment)
                ?.ToString();

            if (kindName is not null && kindName.Contains("Frozen", StringComparison.OrdinalIgnoreCase))
                list.Add((segment.Start, segment.End));
        }
        return list;
    }

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

    private static IReadOnlyList<DuplicateStringSnapshot> DrainToDescendingWaste(
        PriorityQueue<StringLeakInfo, ulong> pq)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            list.Add(new DuplicateStringSnapshot(info.Preview ?? string.Empty, info.Count, wasted));
        }
        list.Reverse();
        return list;
    }

    private static IReadOnlyList<DuplicateStringSnapshot> DrainToDescendingCount(
        PriorityQueue<StringLeakInfo, int> pq)
    {
        var list = new List<DuplicateStringSnapshot>(pq.Count);
        while (pq.Count > 0)
        {
            StringLeakInfo info = pq.Dequeue();
            ulong wasted = info.TotalSize - (info.TotalSize / (ulong)info.Count);
            list.Add(new DuplicateStringSnapshot(info.Preview ?? string.Empty, info.Count, wasted));
        }
        list.Reverse();
        return list;
    }

    private static StringFingerprint CreateFingerprint(string value)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime  = 1099511628211UL;

        ulong hash = fnvOffset;
        foreach (char c in value)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return new StringFingerprint(hash, value.Length, value[0], value[^1]);
    }

    private static string CreatePreview(string value)
    {
        string preview = value.Length > 47 ? value[..47] + "..." : value;
        return preview.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private readonly record struct StringFingerprint(ulong Hash, int Length, char FirstChar, char LastChar);

    public void Dispose() { }
}
