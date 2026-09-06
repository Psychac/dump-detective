using DumpDetective.Analysis.Indexing.Container;
using DumpDetective.Analysis.Indexing.Dominator;
using DumpDetective.Analysis.Indexing.ReverseIndex;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Tests.Helpers;

/// <summary>
/// Drives the real Phase A → B → C reverse-edge pipeline (<see cref="ReverseEdgeExtractor"/> →
/// <see cref="ReverseEdgeCsrBuilder"/> → <see cref="ReverseEdgeContainerWriter"/>) from a plain list
/// of synthetic edges, plus the <c>DominatorReachableAddresses</c> companion section format v8's CSR
/// depends on for address↔row resolution (docs/cache/cache-format-clean-slate-redesign.md §2) —
/// production always has it because the same walk that extracts these edges also produces it, but a
/// test building edges directly has to supply it explicitly.
/// </summary>
internal static class ReverseEdgeCsrTestWriter
{
    /// <summary>
    /// The reachable-address set is derived from the edges themselves — every address appearing as
    /// either endpoint — matching the guarantee <c>ReachableGraphWalker</c> provides in production
    /// (an edge is only ever recorded for two addresses the walk has itself visited).
    /// </summary>
    public static async Task WriteAsync(
        CacheContainerWriter containerWriter,
        string scratchDir,
        int bucketCount,
        (ulong Parent, ulong Child)[] edges,
        IProgress<AnalyzerProgressReport>? progress = null)
    {
        var extractor = new ReverseEdgeExtractor(bucketCount, scratchDir);
        foreach ((ulong parent, ulong child) in edges)
            extractor.RecordEdge(parent, child);
        await extractor.DisposeAsync(progress);

        ulong[] sortedReachableAddresses = edges
            .SelectMany(e => new[] { e.Parent, e.Child })
            .Distinct()
            .OrderBy(a => a)
            .ToArray();

        DominatorReachableAddressWriter.Write(containerWriter, sortedReachableAddresses);

        ReverseEdgeCsrResult csr = await ReverseEdgeCsrBuilder.BuildAsync(
            scratchDir, bucketCount, sortedReachableAddresses, CancellationToken.None, progress);

        ReverseEdgeContainerWriter.Write(containerWriter, csr, progress);
    }

    /// <summary>Synchronous convenience wrapper for call sites that don't want to become async.</summary>
    public static void Write(
        CacheContainerWriter containerWriter,
        string scratchDir,
        int bucketCount,
        (ulong Parent, ulong Child)[] edges,
        IProgress<AnalyzerProgressReport>? progress = null) =>
        WriteAsync(containerWriter, scratchDir, bucketCount, edges, progress).GetAwaiter().GetResult();
}
