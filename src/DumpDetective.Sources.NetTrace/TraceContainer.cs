using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Builds a trace's own container file, opens it, and exposes the 3 trace query surfaces backed by
/// it — the sole public(-ish; <c>internal</c>, IVT'd like everything else in this project) way for
/// <c>DumpDetective.Cli</c>'s pipeline nodes to work with a trace container, so
/// <see cref="CacheContainerReader"/> (a <c>DumpDetective.Platform</c>-internal type this project
/// has its own <c>InternalsVisibleTo</c> grant for) never has to leak across that project boundary —
/// see docs/refactor/modularity/phase-4-session-orchestration.md migration step 5f.
/// </summary>
internal sealed class TraceContainer
{
    private readonly string _containerPath;

    public ITraceCpuSampleQuery CpuSamples { get; }
    public ITraceGcPauseQuery GcPauses { get; }
    public ITraceContentionQuery Contention { get; }

    private TraceContainer(string containerPath, CacheContainerReader reader)
    {
        _containerPath = containerPath;
        CpuSamples = new TraceCpuSampleQuery(reader);
        GcPauses = new TraceGcPauseQuery(reader);
        Contention = new TraceContentionQuery(reader);
    }

    /// <summary>
    /// Streams <paramref name="tracePath"/> into a temp container and opens it. Ports
    /// <c>TraceAnalysisRunner</c>'s own section-selection logic unchanged (dropping
    /// <see cref="CacheSectionId.TraceCpuSamples"/> for a non-ETL capture; pulling in
    /// <see cref="CacheSectionId.TraceMethods"/> alongside it when ETL, since
    /// <see cref="TraceCpuSampleQuery"/> resolves samples against method address ranges).
    /// </summary>
    public static TraceContainer Build(string tracePath, CancellationToken cancellationToken)
    {
        string containerPath = Path.Combine(Path.GetTempPath(), $"trace-session-{Guid.NewGuid():N}.bin");

        var neededSections = new HashSet<CacheSectionId>
        {
            CacheSectionId.TraceGcEvents,
            CacheSectionId.TraceContention,
            CacheSectionId.TraceCpuSamples,
        };

        bool isEtl = Path.GetExtension(tracePath).Equals(".etl", StringComparison.OrdinalIgnoreCase);
        if (!isEtl)
            neededSections.Remove(CacheSectionId.TraceCpuSamples);
        else
            neededSections.Add(CacheSectionId.TraceMethods);

        TraceIndexBuilder.Build(tracePath, containerPath, targetProcessId: null, cancellationToken, sections: neededSections);

        if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            throw new InvalidOperationException($"Trace container built at '{containerPath}' but could not be reopened.");

        return new TraceContainer(containerPath, reader);
    }

    /// <summary>No cache-hit reuse yet (see the deleted <c>TraceAnalysisRunner</c>'s own remarks on
    /// this same simplification) — every container built so far is single-use.</summary>
    public void DeleteBackingFile()
    {
        if (File.Exists(_containerPath))
            File.Delete(_containerPath);
    }
}
