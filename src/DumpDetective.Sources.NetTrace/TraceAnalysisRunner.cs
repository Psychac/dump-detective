using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// The trace-only side of the interim router accepted as deliberate debt in
/// docs/refactor/modularity-plan.md § 8 ("Accept an interim router... not a permanent design") and
/// named more specifically in docs/refactor/modularity/phase-6-trace-source.md § Phase 6b ("needs
/// Phase 1 SDK contracts... and *some* session router"). This is that router's trace-only half:
/// build the trace's own container (Phase 6a), run every registered <see cref="ITraceAnalyzer"/>
/// over it, hand back the observations. No session/artifact model, no capability resolution, no
/// combined dump+trace path — those are Phase 4's job and are exactly what this is standing in for
/// until that phase is scheduled.
/// </summary>
/// <remarks>
/// The container is built to a temp file and deleted afterward — no cache-hit reuse of a
/// previously-built trace container yet, unlike the dump side's <c>cache.bin</c>. A named
/// simplification, not an oversight: reuse needs a cache-key strategy for trace files (content hash
/// vs. path+mtime, same question <c>DumpContentHasher</c> answers for dumps) that hasn't been
/// designed, and every trace container built so far in this phase has been for a single test run
/// anyway.
/// </remarks>
internal static class TraceAnalysisRunner
{
    private static readonly IReadOnlyList<ITraceAnalyzer> Analyzers =
        [new GcPauseAnalyzer(), new ContentionAnalyzer(), new CpuHotspotAnalyzer()];

    public static IReadOnlyList<Observation> Run(string tracePath, int? targetProcessId, CancellationToken cancellationToken)
    {
        string containerPath = Path.Combine(Path.GetTempPath(), $"trace-session-{Guid.NewGuid():N}.bin");
        try
        {
            var neededSections = new HashSet<CacheSectionId>();
            foreach (ITraceAnalyzer analyzer in Analyzers)
                neededSections.UnionWith(analyzer.RequiredSections);

            // trace.cpu-samples is ETW-only — see CpuSampleIndexer's remarks. TraceIndexBuilder.Build
            // treats any requested-section write failure as fatal to the whole container (a real
            // indexer bug should be loud), so a categorically-unsupported section must be dropped
            // from the request here rather than left to fail and take the other sections down with
            // it. CpuHotspotAnalyzer.Analyze already degrades gracefully to "no observations" when
            // its section is simply absent.
            if (!Path.GetExtension(tracePath).Equals(".etl", StringComparison.OrdinalIgnoreCase))
                neededSections.Remove(CacheSectionId.TraceCpuSamples);

            TraceIndexBuilder.Build(tracePath, containerPath, targetProcessId, cancellationToken, sections: neededSections);

            if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
                throw new InvalidOperationException($"Trace container built at '{containerPath}' but could not be reopened.");

            var artifactId = new ArtifactId(tracePath);
            var observations = new List<Observation>();
            foreach (ITraceAnalyzer analyzer in Analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observations.AddRange(analyzer.Analyze(reader, artifactId));
            }

            return observations;
        }
        finally
        {
            if (File.Exists(containerPath))
                File.Delete(containerPath);
        }
    }
}
