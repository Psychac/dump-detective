using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Orchestration.Session;

/// <summary>
/// Builds an <see cref="AnalysisSession"/> from already-probed <see cref="ArtifactDescriptor"/>s —
/// see docs/refactor/modularity/phase-4-session-orchestration.md migration step 2.
/// </summary>
/// <remarks>
/// Deliberately takes descriptors, not raw paths: probing a path into a descriptor (reading a dump's
/// header, a trace's event stream, ...) needs source-specific knowledge this project keeps out of
/// <c>DumpDetective.Orchestration</c> (Sdk/Platform dependencies only). Callers that own that
/// source-specific knowledge build the descriptor and hand it here; this type's only job is
/// combining N of them into one session's capability set.
/// </remarks>
public static class SessionBuilder
{
    public static AnalysisSession Build(IReadOnlyList<ArtifactDescriptor> artifacts)
    {
        if (artifacts.Count == 0)
            throw new ArgumentException("A session needs at least one artifact.", nameof(artifacts));

        HashSet<Capability> available = [];
        foreach (ArtifactDescriptor artifact in artifacts)
            available.UnionWith(artifact.Provides);

        return new AnalysisSession(artifacts, available);
    }
}
