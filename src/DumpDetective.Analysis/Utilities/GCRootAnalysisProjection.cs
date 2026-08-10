using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;

namespace DumpDetective.Analysis.Utilities;

internal sealed record GCRootAnalysisProjectionResult(
    IReadOnlyList<RootKindSummary> ByKind,
    List<RootFinding> FindingsBySeverityDescending);

internal static class GCRootAnalysisProjection
{
    public static GCRootAnalysisProjectionResult Build(
        IReadOnlyList<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots,
        ClrHeap heap,
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates)
    {
        ulong totalHeapBytes = 0;
        foreach (TypeAggregateIndexEntry agg in aggregates.Values)
            totalHeapBytes += agg.TotalSize;

        var kindCounts = new Dictionary<string, int>(8);
        var kindBytes = new Dictionary<string, ulong>(8);
        var findings = new List<RootFinding>(roots.Count);

        for (int i = 0; i < roots.Count; i++)
        {
            (ulong targetAddr, ulong rootAddr, byte rawKind) = roots[i];

            ClrObject obj = GetObject(targetAddr, heap);
            ulong estimate = EstimateRetainedBytes(obj, aggregates);
            if (estimate == 0)
                continue;

            string kind = RootIndexReader.KindToString(rawKind);
            kindCounts[kind] = (kindCounts.TryGetValue(kind, out int count) ? count : 0) + 1;
            kindBytes[kind] = (kindBytes.TryGetValue(kind, out ulong bytes) ? bytes : 0UL) + estimate;

            string targetType = ResolveTypeName(obj);
            int severity = ComputeSeverity(estimate, kind);

            findings.Add(new RootFinding(
                RootKind: kind,
                RootAddress: rootAddr,
                FieldDescription: null,
                TargetTypeName: targetType,
                TargetAddress: targetAddr,
                EstimatedRetainedBytes: estimate,
                SeverityScore: severity));
        }

        var byKind = new List<RootKindSummary>(kindCounts.Count);
        foreach (KeyValuePair<string, int> kv in kindCounts)
        {
            string kind = kv.Key;
            ulong estBytes = kindBytes.TryGetValue(kind, out ulong kb) ? kb : 0UL;
            double pct = totalHeapBytes > 0 ? (double)estBytes / totalHeapBytes * 100.0 : 0.0;
            byKind.Add(new RootKindSummary(kind, kv.Value, estBytes, pct));
        }
        byKind.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        findings.Sort(static (a, b) => b.SeverityScore.CompareTo(a.SeverityScore));

        return new GCRootAnalysisProjectionResult(byKind, findings);
    }

    private static ClrObject GetObject(ulong targetAddr, ClrHeap heap)
    {
        if (targetAddr == 0)
            return default;

        try
        {
            return heap.GetObject(targetAddr);
        }
        catch
        {
            return default;
        }
    }

    private static ulong EstimateRetainedBytes(
        ClrObject obj,
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates)
    {
        if (!obj.IsValid || obj.Type is null)
            return 0;

        ulong mt = obj.Type.MethodTable;
        if (mt != 0 && aggregates.TryGetValue(mt, out TypeAggregateIndexEntry agg) && agg.Count > 0)
            return agg.TotalSize / (ulong)agg.Count; // avg size as single-object estimate

        return obj.Size;
    }

    private static string ResolveTypeName(ClrObject obj)
    {
        if (obj.IsValid && obj.Type?.Name is string name)
            return name;

        return obj.Address != 0 ? $"0x{obj.Address:X}" : "(unknown)";
    }

    private static int ComputeSeverity(ulong retainedBytes, string kind)
    {
        // Base score from retained size (log-scaled)
        int baseScore = retainedBytes switch
        {
            >= 100_000_000 => 100,
            >= 10_000_000 => 80,
            >= 1_000_000 => 60,
            >= 100_000 => 40,
            >= 10_000 => 20,
            _ => 5
        };

        // Kind multiplier: static/global roots are hardest to release
        int multiplier = kind switch
        {
            "StrongHandle" => 3,
            "FinalizerQueue" => 2,
            "PinnedHandle" => 2,
            "Stack" => 1,
            _ => 1
        };

        return Math.Min(baseScore * multiplier, 300);
    }
}
