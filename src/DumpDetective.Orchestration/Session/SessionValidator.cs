using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.Orchestration.Session;

/// <summary>
/// Structural pre-flight checks over a session's artifacts before a graph is built from them —
/// see docs/refactor/modularity/phase-4-session-orchestration.md migration step 4.
/// </summary>
/// <remarks>
/// Deliberately narrow: <see cref="ArtifactDescriptor.Process"/>/<see cref="ArtifactDescriptor.CapturedAt"/>
/// are still placeholder data today (see <c>SingleDumpArtifactFactory</c>'s remarks and step 2's
/// known gap), so a process-identity or capture-time compatibility check would reject legitimate
/// trend runs — trend dump files are routinely named per-snapshot, not per-process. Only checks
/// safe to make on today's data land here; identity/alignment checks wait for real per-artifact
/// probing, per step 2's known-gap note.
/// </remarks>
public static class SessionValidator
{
    public static void Validate(IReadOnlyList<ArtifactDescriptor> artifacts)
    {
        if (artifacts.Count == 0)
            throw new SessionValidationException("A session needs at least one artifact.");

        string sourceKind = artifacts[0].SourceKind;
        string[] mismatchedPaths = artifacts
            .Where(a => !string.Equals(a.SourceKind, sourceKind, StringComparison.Ordinal))
            .Select(a => a.Path)
            .ToArray();
        if (mismatchedPaths.Length > 0)
            throw new SessionValidationException(
                $"A session's artifacts must all share one source kind ('{sourceKind}'); " +
                $"found a different kind in: {string.Join(", ", mismatchedPaths)}.");

        string[] duplicatePaths = artifacts
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePaths.Length > 0)
            throw new SessionValidationException(
                $"A session's artifacts must be distinct paths; duplicated: {string.Join(", ", duplicatePaths)}.");
    }
}

public sealed class SessionValidationException(string message) : Exception(message);
