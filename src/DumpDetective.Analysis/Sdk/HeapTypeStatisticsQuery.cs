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

    public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics()
    {
        if (cache is IHeapIndexBuilder builder && builder.TryGetHeapIndex(out HeapIndexBuildResult? heapIndex))
            return BuildFromIndex(heapIndex.TypeAggregates);

        return BuildFromCachedStatistics(cache.GetOrBuildTypeStatistics(heap));
    }

    private Dictionary<string, HeapTypeStatistics> BuildFromIndex(IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates)
    {
        var result = new Dictionary<string, HeapTypeStatistics>(aggregates.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
        {
            ulong methodTable = kv.Key;
            TypeAggregateIndexEntry e = kv.Value;
            string name = heap.GetTypeByMethodTable(methodTable)?.Name ?? $"MT:0x{methodTable:x}";

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
                IsFinalizableType: (e.Flags & TypeAggregateFlags.IsFinalizableType) != 0);
        }
        return result;
    }

    private static Dictionary<string, HeapTypeStatistics> BuildFromCachedStatistics(Dictionary<string, CachedTypeStatistics> stats)
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
                IsFinalizableType: false);
        }
        return result;
    }

    private static TypeRef ToTypeRef(string rawName, ulong? methodTable)
    {
        (string canonicalName, MatchFidelity fidelity) = EntityCanonicalizer.CanonicalizeTypeName(rawName);
        return new TypeRef { CanonicalName = canonicalName, Fidelity = fidelity, MethodTable = methodTable };
    }

    public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals()
    {
        AnalyzerHelpers.ComputeExactGenBytes(heap, out ulong gen0Bytes, out ulong gen1Bytes, out ulong gen2Bytes);
        return (gen0Bytes, gen1Bytes, gen2Bytes);
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
