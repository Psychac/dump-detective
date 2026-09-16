using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Temporal;

namespace DumpDetective.Cli.Execution;

/// <summary>
/// Builds the <see cref="ArtifactDescriptor"/> for the single dump path being analyzed — see
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 2.
/// </summary>
/// <remarks>
/// Analyzer selection (<see cref="DumpAnalysisService.ExecuteAsync"/>) happens before
/// <c>LoadDumpNode</c> loads the dump, so no real ClrMD data — process identity, actual capability
/// availability — exists yet at this point. <see cref="DumpCapabilities"/> is therefore a static
/// "every dump capability" set, not a per-dump probe: exactly what the whole single-dump path has
/// always implicitly assumed (every analyzer catalog member is dump-capable today), just made
/// explicit so <c>CapabilityResolver</c> has something real to check against. <see cref="Create"/>'s
/// <see cref="ArtifactDescriptor.Process"/>/<see cref="ArtifactDescriptor.CapturedAt"/> are similarly
/// best-effort placeholders (a real per-dump probe is out of scope until multi-artifact sessions
/// make process/capture-time correlation load-bearing — migration step 3).
/// </remarks>
internal static class SingleDumpArtifactFactory
{
    private static readonly IReadOnlySet<Capability> DumpCapabilities = CapabilityVocabulary.Known
        .Where(key => !key.StartsWith("trace.", StringComparison.Ordinal) && !key.StartsWith("temporal.", StringComparison.Ordinal))
        .Select(key => new Capability(key))
        .ToHashSet();

    public static ArtifactDescriptor Create(string dumpPath) => new()
    {
        Id = new ArtifactId(dumpPath),
        SourceKind = "clr-dump",
        Path = dumpPath,
        CapturedAt = new TimeAnchor
        {
            WallClockUtc = File.GetLastWriteTimeUtc(dumpPath),
            Confidence = AnchorConfidence.Approximate,
        },
        Process = new ProcessIdentity
        {
            ProcessId = 0,
            ImageName = Path.GetFileNameWithoutExtension(dumpPath),
        },
        Provides = DumpCapabilities,
    };
}
