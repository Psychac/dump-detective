using System.Text.Json;

using DumpDetective.Analysis.Indexing.Container;

namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// Reads the <c>DominatorTreeMetadata</c> section (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) written by
/// <see cref="DominatorTreeMetadataWriter"/>. Mirrors <c>ForwardEdgeIndexReader</c>'s JSON-section
/// read pattern.
/// </summary>
internal static class DominatorTreeMetadataReader
{
    public static bool TryOpen(CacheContainerReader container, out DominatorTreeMetadata? metadata)
    {
        metadata = null;

        if (!container.TryOpenSection(CacheSectionId.DominatorTreeMetadata, out Stream? stream) || stream is null)
            return false;

        using (stream)
        {
            try
            {
                metadata = JsonSerializer.Deserialize<DominatorTreeMetadata>(stream);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return metadata is not null;
    }
}
