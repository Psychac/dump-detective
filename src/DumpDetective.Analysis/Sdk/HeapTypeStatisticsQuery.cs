using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>heap.types</c> capability — the first Tier-1 surface with a
/// real implementation, built for the Phase 1 retyping pilot
/// (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md). Lives here rather than a
/// dedicated <c>Sources.ClrDump</c> project because that project doesn't exist yet; this type is
/// written so it can move there unchanged once it does.
/// </summary>
/// <remarks>
/// Mirrors <see cref="GCGenerationAnalyzer"/>'s (soon <c>Analyzers.GCGenerationAnalyzer</c>'s) old
/// fast/fallback fork exactly: prefers the prebuilt <c>TypeAggregates</c> index
/// (<see cref="HasExactGenerationData"/> <c>true</c>), falling back to <see cref="IHeapAnalysisCache.GetOrBuildTypeStatistics"/>
/// (no per-generation breakdown) when no index was built. Known, accepted gap: the index path is
/// keyed by <c>MethodTable</c> — a dump-local handle — and dictionary-keyed here by resolved type
/// *name*; two distinct method tables that resolve to the same display name (a real but rare
/// possibility, e.g. same simple name loaded into two ALCs) collapse to one dictionary entry, last
/// write wins. The pilot's own characterization test doesn't exercise this case, and per this
/// project's hard-need-basis convention it isn't handled speculatively — revisit only if a real
/// dump surfaces it.
/// </remarks>
internal sealed class HeapTypeStatisticsQuery(ClrHeap heap, IHeapAnalysisCache cache) : IHeapTypeStatisticsQuery
{
    public bool HasExactGenerationData =>
        cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out _);

    public bool HasTypeShapeData =>
        cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out HeapIndexBuildResult? idx) && idx.TypeShapeCache is not null;

    public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics()
    {
        if (cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
            return BuildFromIndex(heapIndex);

        return BuildFromCachedStatistics(cache, cache.GetOrBuildTypeStatistics(heap));
    }

    private Dictionary<string, HeapTypeStatistics> BuildFromIndex(HeapIndexBuildResult heapIndex)
    {
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates = heapIndex.TypeAggregates;
        IReadOnlyDictionary<ulong, TypeShapeEntry>? shapes = heapIndex.TypeShapeCache;

        var result = new Dictionary<string, HeapTypeStatistics>(aggregates.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
        {
            ulong methodTable = kv.Key;
            TypeAggregateIndexEntry e = kv.Value;
            ClrType? type = heap.GetTypeByMethodTable(methodTable);
            string name = type?.Name ?? $"MT:0x{methodTable:x}";

            int totalFields = 0, refFields = 0, valFields = 0, baseDepth = 0, ifaceCount = 0;
            if (type is not null && shapes is not null && shapes.TryGetValue(methodTable, out TypeShapeEntry shape))
            {
                totalFields = shape.TotalFields;
                refFields = shape.RefFields;
                valFields = shape.ValFields;
                baseDepth = ComputeBaseTypeDepth(type);
                try { ifaceCount = type.EnumerateInterfaces().Count(); }
                catch { /* corrupt/unusual type -- defaults to 0, matches the pre-retyping analyzer's own defensive catch */ }
            }

            result[name] = new HeapTypeStatistics(
                Type: ToTypeRef(name, methodTable),
                InstanceCount: e.Count,
                TotalSize: e.TotalSize,
                LohCount: e.LohCount,
                LohSize: e.LohSize,
                Gen0Count: e.Gen0Count,
                Gen1Count: e.Gen1Count,
                Gen2Count: e.Gen2Count,
                Gen2TotalSize: e.Gen2TotalSize,
                IsFinalizableType: (e.Flags & TypeAggregateFlags.IsFinalizableType) != 0,
                SampleAddress: e.SampleAddress,
                ModuleName: TypeAggregateNameResolver.ResolveModuleName(heap, methodTable, e.SampleAddress),
                TotalFields: totalFields,
                ReferenceFields: refFields,
                ValueFields: valFields,
                IsValueType: type?.IsValueType ?? false,
                IsArray: type?.IsArray ?? false,
                BaseTypeChainDepth: baseDepth,
                InterfaceCount: ifaceCount);
        }
        return result;
    }

    // Matches the pre-retyping ObjectShapeAnalyzer's own cap exactly.
    private static int ComputeBaseTypeDepth(ClrType type)
    {
        int depth = 0;
        ClrType? cur = type.BaseType;
        while (cur is not null && depth < 20)
        {
            depth++;
            cur = cur.BaseType;
        }
        return depth;
    }

    private static Dictionary<string, HeapTypeStatistics> BuildFromCachedStatistics(IHeapAnalysisCache cache, Dictionary<string, CachedTypeStatistics> stats)
    {
        var result = new Dictionary<string, HeapTypeStatistics>(stats.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, CachedTypeStatistics> kv in stats)
        {
            CachedTypeStatistics s = kv.Value;
            result[kv.Key] = new HeapTypeStatistics(
                Type: ToTypeRef(kv.Key, methodTable: null),
                InstanceCount: s.Count,
                TotalSize: s.TotalSize,
                LohCount: s.LohCount,
                LohSize: s.LohSize,
                Gen0Count: 0,
                Gen1Count: 0,
                Gen2Count: 0,
                Gen2TotalSize: 0,
                IsFinalizableType: false,
                SampleAddress: cache.GetSampleInstanceAddress(kv.Key) ?? 0,
                ModuleName: s.ModuleName);
        }
        return result;
    }

    private static TypeRef ToTypeRef(string rawName, ulong? methodTable) => SdkTypeRefFactory.FromName(rawName, methodTable);

    public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals()
    {
        AnalyzerHelpers.ComputeExactGenBytes(heap, out ulong gen0Bytes, out ulong gen1Bytes, out ulong gen2Bytes);
        return (gen0Bytes, gen1Bytes, gen2Bytes);
    }

    public long? ExactObjectCount =>
        cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out HeapIndexBuildResult? idx) ? idx.ObjectCount : null;

    public ulong GetTotalIndexedBytes()
    {
        if (cache is not IHeapIndexBuilder builder || !builder.TryGetHeapIndex(out HeapIndexBuildResult? idx))
            return 0;

        ulong total = 0;
        foreach (TypeAggregateIndexEntry e in idx.TypeAggregates.Values)
            total += e.TotalSize;
        return total;
    }

    public IReadOnlyList<TypeRef>? TryGetDistinctTypes()
    {
        IReadOnlyList<ulong>? methodTables = cache.TryGetDistinctMethodTables();
        if (methodTables is null)
            return null;

        var result = new List<TypeRef>(methodTables.Count);
        foreach (ulong mt in methodTables)
        {
            string name = heap.GetTypeByMethodTable(mt)?.Name ?? $"MT:0x{mt:x}";
            result.Add(ToTypeRef(name, mt));
        }
        return result;
    }

    public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => cache.TryGetGlobalSizeBuckets();
}
