using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Utilities;

internal sealed record GCRootAnalysisProjectionResult(
    IReadOnlyList<RootKindSummary> ByKind,
    List<RootFinding> FindingsBySeverityDescending);

internal static class GCRootAnalysisProjection
{
    public static GCRootAnalysisProjectionResult Build(
        IReadOnlyList<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots,
        ClrHeap heap,
        IHeapAnalysisCache cache,
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

            if (targetAddr == 0 || !cache.TryGetObjectMetadata(heap, targetAddr, out ulong methodTable, out ulong size) || size == 0)
                continue;

            string kind = RootIndexReader.KindToString(rawKind);
            kindCounts[kind] = (kindCounts.TryGetValue(kind, out int count) ? count : 0) + 1;
            kindBytes[kind] = (kindBytes.TryGetValue(kind, out ulong bytes) ? bytes : 0UL) + size;

            string targetType = ResolveTypeName(heap, methodTable, targetAddr);
            int severity = ComputeSeverity(size, kind);

            findings.Add(new RootFinding(
                RootKind: kind,
                RootAddress: rootAddr,
                FieldDescription: null,
                TargetTypeName: targetType,
                TargetAddress: targetAddr,
                EstimatedRetainedBytes: size,
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

    private static string ResolveTypeName(ClrHeap heap, ulong methodTable, ulong targetAddr)
    {
        if (methodTable != 0 && heap.GetTypeByMethodTable(methodTable)?.Name is string name)
            return name;

        return targetAddr != 0 ? $"0x{targetAddr:X}" : "(unknown)";
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
