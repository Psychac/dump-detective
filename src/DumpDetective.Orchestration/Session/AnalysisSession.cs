using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Orchestration.Session;

/// <summary>
/// One analysis run's set of input artifacts and the capabilities they jointly provide — see
/// docs/refactor/modularity/phase-4-session-orchestration.md. A single-dump session (today's only
/// caller) carries exactly one <see cref="ArtifactDescriptor"/>; multi-artifact sessions (trend,
/// combined dump+trace) are migration step 3's generalization, not this type's.
/// </summary>
public sealed record AnalysisSession(
    IReadOnlyList<ArtifactDescriptor> Artifacts,
    IReadOnlySet<Capability> AvailableCapabilities);
