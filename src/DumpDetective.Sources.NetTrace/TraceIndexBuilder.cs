using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Builds a trace's own container file — one container per artifact, per
/// docs/refactor/modularity/phase-2-artifact-platform.md, never sharing a file with a dump's
/// <c>cache.bin</c>. Four sections so far (<c>trace.methods</c>, <c>trace.gcevents</c>,
/// <c>trace.contention</c>, <c>trace.cpu-samples</c>); more join this same container as later
/// increments of Phase 6a land.
/// </summary>
/// <remarks>
/// Each section streams the trace file independently — <paramref name="tracePath"/> below is read
/// once per section, not once total. This is a known, named simplification, not an oversight: the
/// sibling-implementation note in docs/refactor/modularity-plan.md § 11 calls out a single-pass,
/// many-consumer dispatcher as the eventual shape for <c>IndexAsync</c>, but that needs at least
/// two of these three outputs buffered off to the side while one trace pass runs (container
/// sections can only be written one at a time — see <c>CacheContainerWriter.BeginSection</c>).
/// Given the streaming per-section cost measured so far (24 s for trace.methods alone against a
/// real 912.1 MB capture), three independent passes is the proportionate choice for this
/// increment; consolidating into one fan-out pass is deferred until trace size or section count
/// makes the wall-clock cost of separate passes the actual bottleneck.
/// </remarks>
internal static class TraceIndexBuilder
{
    /// <summary>
    /// Streams <paramref name="tracePath"/> once per section in <paramref name="sections"/> and
    /// writes <paramref name="containerPath"/>.
    /// </summary>
    /// <param name="sections">
    /// Which sections to build. Defaults to all three — callers that know they only need a subset
    /// (e.g. <c>TraceAnalysisRunner</c>, which only runs analyzers reading
    /// <see cref="CacheSectionId.TraceGcEvents"/>/<see cref="CacheSectionId.TraceContention"/> today)
    /// should pass that subset: each section is its own independent streaming pass (see remarks
    /// above), so skipping an unneeded one is a real, proportionate saving — measured at over half
    /// the wall-clock of a real trace-only run when <see cref="CacheSectionId.TraceMethods"/> was
    /// always built regardless of whether anything read it.
    /// </param>
    public static void Build(
        string tracePath,
        string containerPath,
        int? targetProcessId,
        CancellationToken cancellationToken = default,
        IProgress<AnalyzerProgressReport>? progress = null,
        IReadOnlySet<CacheSectionId>? sections = null)
    {
        sections ??= AllSections;

        using var containerWriter = new CacheContainerWriter(containerPath, dumpPath: null, progress);
        List<string> warnings = [];

        if (sections.Contains(CacheSectionId.TraceMethods))
        {
            containerWriter.TryWriteSection(
                CacheSectionId.TraceMethods,
                "indexing trace methods",
                stream => TraceMethodIndexer.Write(stream, tracePath, targetProcessId, cancellationToken),
                warnings);
        }

        if (sections.Contains(CacheSectionId.TraceGcEvents))
        {
            containerWriter.TryWriteSection(
                CacheSectionId.TraceGcEvents,
                "indexing trace GC pauses",
                stream => GcPauseIndexer.Write(stream, tracePath, targetProcessId, cancellationToken),
                warnings);
        }

        if (sections.Contains(CacheSectionId.TraceContention))
        {
            containerWriter.TryWriteSection(
                CacheSectionId.TraceContention,
                "indexing trace contention",
                stream => ContentionIndexer.Write(stream, tracePath, targetProcessId, cancellationToken),
                warnings);
        }

        if (sections.Contains(CacheSectionId.TraceCpuSamples))
        {
            containerWriter.TryWriteSection(
                CacheSectionId.TraceCpuSamples,
                "indexing trace CPU samples",
                stream => CpuSampleIndexer.Write(stream, tracePath, targetProcessId, cancellationToken),
                warnings);
        }

        containerWriter.Finish();

        if (warnings.Count > 0)
            throw new InvalidOperationException($"trace section(s) failed: {string.Join("; ", warnings)}");
    }

    private static readonly IReadOnlySet<CacheSectionId> AllSections = new HashSet<CacheSectionId>
    {
        CacheSectionId.TraceMethods, CacheSectionId.TraceGcEvents, CacheSectionId.TraceContention, CacheSectionId.TraceCpuSamples,
    };
}
