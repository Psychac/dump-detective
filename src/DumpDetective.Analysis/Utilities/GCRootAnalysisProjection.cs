using Microsoft.Diagnostics.Runtime;

using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Traversal.Dominator;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;

namespace DumpDetective.Analysis.Utilities;

internal sealed record GCRootAnalysisProjectionResult(
    IReadOnlyList<RootKindSummary> ByKind,
    List<RootFinding> FindingsBySeverityDescending,
    int DroppedZeroEstimateRootCount);

internal static class GCRootAnalysisProjection
{
    /// <param name="treeProvider">
    /// §12.1 (docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md): when available,
    /// upgrades both per-<see cref="RootFinding"/> and per-kind retained-byte totals from shallow
    /// size to exact dominator-tree retained bytes. <c>null</c> (legacy cache.bin, Stage B not gated
    /// on, or Stage A/B failed to persist) falls back to today's shallow-size behavior unchanged.
    /// </param>
    public static GCRootAnalysisProjectionResult Build(
        IReadOnlyList<(ulong TargetAddr, ulong RootAddr, byte Kind)> roots,
        ClrHeap heap,
        IHeapAnalysisCache cache,
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
        IDominatorTreeProvider? treeProvider = null)
    {
        ulong totalHeapBytes = 0;
        foreach (TypeAggregateIndexEntry agg in aggregates.Values)
            totalHeapBytes += agg.TotalSize;

        var kindCounts = new Dictionary<string, int>(8);
        var kindBytes = new Dictionary<string, ulong>(8);
        // Index: 0 = Gen0, 1 = Gen1, 2 = Gen2, 3 = Loh. Poh/Frozen/Unknown targets are counted
        // toward kindCounts but not toward any generation bucket (audit only asked for Gen0-2/LOH).
        var kindGenerationCounts = new Dictionary<string, int[]>(8);
        Dictionary<string, List<ulong>>? targetsByKind = treeProvider is not null ? new Dictionary<string, List<ulong>>(8) : null;
        var findings = new List<RootFinding>(roots.Count);
        Dictionary<ulong, (string TypeName, string FieldName, int AppDomainId)>? staticFieldsByRootAddress = null;
        int droppedZeroEstimateRootCount = 0;

        for (int i = 0; i < roots.Count; i++)
        {
            (ulong targetAddr, ulong rootAddr, byte rawKind) = roots[i];

            // Dropped silently before P2-5: a null target, unresolvable object metadata, or a
            // zero-size object all mean this root contributes no retained-byte estimate at all —
            // surfaced as a single count on GCRootDomainResult rather than left invisible.
            if (targetAddr == 0 || !cache.TryGetObjectMetadata(heap, targetAddr, out ulong methodTable, out ulong size) || size == 0)
            {
                droppedZeroEstimateRootCount++;
                continue;
            }

            string kind = RootIndexReader.KindToString(rawKind);
            kindCounts[kind] = (kindCounts.TryGetValue(kind, out int count) ? count : 0) + 1;
            kindBytes[kind] = (kindBytes.TryGetValue(kind, out ulong bytes) ? bytes : 0UL) + size;

            if (!kindGenerationCounts.TryGetValue(kind, out int[]? genCounts))
                kindGenerationCounts[kind] = genCounts = new int[4];
            switch (GenerationTagResolver.Resolve(heap, targetAddr))
            {
                case GenerationTag.Gen0: genCounts[0]++; break;
                case GenerationTag.Gen1: genCounts[1]++; break;
                case GenerationTag.Gen2: genCounts[2]++; break;
                case GenerationTag.Loh: genCounts[3]++; break;
            }

            if (targetsByKind is not null)
            {
                if (!targetsByKind.TryGetValue(kind, out List<ulong>? targetList))
                    targetsByKind[kind] = targetList = new List<ulong>();
                targetList.Add(targetAddr);
            }

            ulong retainedBytes;
            bool retainedBytesIsExact;
            if (treeProvider is not null && treeProvider.TryGetRetainedBytes(targetAddr, out ulong exactRetainedBytes))
            {
                retainedBytes = exactRetainedBytes;
                retainedBytesIsExact = true;
            }
            else
            {
                retainedBytes = size;
                retainedBytesIsExact = false;
            }

            string targetType = ResolveTypeName(heap, methodTable, targetAddr);
            int severity = ComputeSeverity(retainedBytes, kind);

            string? fieldDescription = null;
            if (kind is "StaticVar" or "ThreadStaticVar")
            {
                staticFieldsByRootAddress ??= cache.GetStaticFieldsByRootAddress(heap);
                if (staticFieldsByRootAddress.TryGetValue(rootAddr, out (string TypeName, string FieldName, int AppDomainId) fieldInfo))
                {
                    fieldDescription = fieldInfo.AppDomainId != 1
                        ? $"{fieldInfo.TypeName}.{fieldInfo.FieldName} [AppDomain#{fieldInfo.AppDomainId}]"
                        : $"{fieldInfo.TypeName}.{fieldInfo.FieldName}";
                }
            }

            findings.Add(new RootFinding(
                RootKind: kind,
                RootAddress: rootAddr,
                FieldDescription: fieldDescription,
                TargetTypeName: targetType,
                TargetAddress: targetAddr,
                EstimatedRetainedBytes: retainedBytes,
                SeverityScore: severity,
                RetainedBytesIsExact: retainedBytesIsExact));
        }

        var byKind = new List<RootKindSummary>(kindCounts.Count);
        foreach (KeyValuePair<string, int> kv in kindCounts)
        {
            string kind = kv.Key;
            ulong estBytes;
            bool isExact = false;
            if (treeProvider is not null && targetsByKind is not null && targetsByKind.TryGetValue(kind, out List<ulong>? targets))
            {
                estBytes = DominatorRetainedSetAggregator.ComputeExclusiveRetainedBytes(treeProvider, targets);
                isExact = true;
            }
            else
            {
                estBytes = kindBytes.TryGetValue(kind, out ulong kb) ? kb : 0UL;
            }

            double pct = totalHeapBytes > 0 ? (double)estBytes / totalHeapBytes * 100.0 : 0.0;

            double gen0Fraction = 0.0, gen1Fraction = 0.0, gen2Fraction = 0.0, lohFraction = 0.0;
            if (kindGenerationCounts.TryGetValue(kind, out int[]? genCounts) && kv.Value > 0)
            {
                gen0Fraction = (double)genCounts[0] / kv.Value;
                gen1Fraction = (double)genCounts[1] / kv.Value;
                gen2Fraction = (double)genCounts[2] / kv.Value;
                lohFraction = (double)genCounts[3] / kv.Value;
            }

            byKind.Add(new RootKindSummary(kind, kv.Value, estBytes, pct, isExact,
                gen0Fraction, gen1Fraction, gen2Fraction, lohFraction));
        }
        byKind.Sort(static (a, b) => b.EstimatedRetainedBytes.CompareTo(a.EstimatedRetainedBytes));

        findings.Sort(static (a, b) => b.SeverityScore.CompareTo(a.SeverityScore));

        return new GCRootAnalysisProjectionResult(byKind, findings, droppedZeroEstimateRootCount);
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
