using DumpDetective.Analysis.Indexing;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Shared computation helpers used by multiple analyzers to avoid code duplication.
/// </summary>
internal static class AnalyzerHelpers
{
    /// <summary>
    /// Approximates per-generation byte totals from <paramref name="aggregates"/> using the
    /// average non-LOH object size per MethodTable multiplied by its per-generation count.
    /// This is the same heuristic applied by both <see cref="GCGenerationAnalyzer"/> and
    /// <see cref="AllocationPatternAnalyzer"/>.
    /// </summary>
    public static void ComputeApproxGenBytes(
        IReadOnlyDictionary<ulong, TypeAggregateIndexEntry> aggregates,
        out ulong gen0Bytes,
        out ulong gen1Bytes,
        out ulong gen2Bytes)
    {
        gen0Bytes = 0;
        gen1Bytes = 0;
        gen2Bytes = 0;

        foreach (KeyValuePair<ulong, TypeAggregateIndexEntry> kv in aggregates)
        {
            TypeAggregateIndexEntry e = kv.Value;
            long nonLohCount = e.Count - e.LohCount;
            if (nonLohCount <= 0) continue;
            ulong nonLohSize = e.TotalSize >= e.LohSize ? e.TotalSize - e.LohSize : 0;
            if (nonLohSize == 0) continue;
            ulong avgSize = nonLohSize / (ulong)nonLohCount;
            gen0Bytes += (ulong)e.Gen0Count * avgSize;
            gen1Bytes += (ulong)e.Gen1Count * avgSize;
            gen2Bytes += (ulong)e.Gen2Count * avgSize;
        }
    }
}
