using DumpDetective.Platform;
using DumpDetective.Platform.Storage.Container;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Builds a trace's own container file — one container per artifact, per
/// docs/refactor/modularity/phase-2-artifact-platform.md, never sharing a file with a dump's
/// <c>cache.bin</c>. First (and currently only) section is <c>trace.methods</c>; more sections
/// join this same container as later increments of Phase 6a land.
/// </summary>
internal static class TraceIndexBuilder
{
    /// <summary>
    /// Streams <paramref name="tracePath"/> once and writes <paramref name="containerPath"/>.
    /// </summary>
    public static void Build(
        string tracePath,
        string containerPath,
        int? targetProcessId,
        CancellationToken cancellationToken = default,
        IProgress<IndexProgress>? progress = null)
    {
        using var containerWriter = new CacheContainerWriter(containerPath, dumpPath: null, progress);
        List<string> warnings = [];

        containerWriter.TryWriteSection(
            CacheSectionId.TraceMethods,
            "indexing trace methods",
            stream => TraceMethodIndexer.Write(stream, tracePath, targetProcessId, cancellationToken),
            warnings);

        containerWriter.Finish();

        if (warnings.Count > 0)
            throw new InvalidOperationException($"trace.methods section failed: {string.Join("; ", warnings)}");
    }
}
