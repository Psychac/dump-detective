using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Traversal.Dominator;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Writes the <c>DominatorTreeMetadata</c> section (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — the one-time O(N)
/// per-<c>MethodTable</c> rollup, computed by <see cref="DominatorRetainedBytesRollup"/> so this
/// stays in sync with <c>DominatorAnalyzer</c>'s Phase 2 equivalent instead of drifting from a
/// second copy of the same aggregation.
/// </summary>
internal static class DominatorTreeMetadataWriter
{
    public static void Write(CacheContainerWriter containerWriter, DominatorRetainedBytesRollupResult rollup)
    {
        var metadata = new DominatorTreeMetadata { TotalRetainedBytes = rollup.TotalRetainedBytes };
        foreach ((ulong methodTable, ulong retainedBytes) in rollup.RetainedBytesByMethodTable)
            metadata.ByMethodTable.Add(new DominatorTypeRetainedBytes { MethodTable = methodTable, RetainedBytes = retainedBytes });

        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(metadata);

        containerWriter.BeginSection(CacheSectionId.DominatorTreeMetadata);
        containerWriter.Stream.Write(jsonBytes, 0, jsonBytes.Length);
        containerWriter.Stream.Flush();
        containerWriter.EndSection(metadata.ByMethodTable.Count);
    }
}
