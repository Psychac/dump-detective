using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Builds the <see cref="ArtifactDescriptor"/> for a trace path being analyzed — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5c. Mirrors
/// <see cref="SingleDumpArtifactFactory"/>'s own simplifications and their rationale exactly:
/// analyzer/capability selection happens before the trace is actually ingested (no
/// <c>TraceIndexBuilder.Build</c> pass has run yet at this point), so <see cref="TraceCapabilities"/>
/// is a static "every trace capability" set, not a per-trace probe. A real probe — e.g.
/// <c>trace.cpu-samples</c> is ETL-only (see <c>CpuSampleIndexer</c>'s remarks), so a <c>.nettrace</c>
/// capture's real <see cref="ArtifactDescriptor.Provides"/> set should be narrower than this — is a
/// later refinement, not this step's job, exactly as <see cref="SingleDumpArtifactFactory"/>'s own
/// remarks defer a real per-dump probe.
/// </summary>
internal static class TraceArtifactFactory
{
    private static readonly IReadOnlySet<Capability> TraceCapabilities = CapabilityVocabulary.Known
        .Where(key => key.StartsWith("trace.", StringComparison.Ordinal))
        .Select(key => new Capability(key))
        .ToHashSet();

    public static ArtifactDescriptor Create(string tracePath) => new()
    {
        Id = new ArtifactId(tracePath),
        SourceKind = "nettrace",
        Path = tracePath,
        CapturedAt = new TimeAnchor
        {
            WallClockUtc = File.GetLastWriteTimeUtc(tracePath),
            Confidence = AnchorConfidence.Approximate,
        },
        Process = new ProcessIdentity
        {
            ProcessId = 0,
            ImageName = Path.GetFileNameWithoutExtension(tracePath),
        },
        Provides = TraceCapabilities,
    };
}
