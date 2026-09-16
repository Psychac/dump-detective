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
///
/// <b>Source-kind rule widened in migration step 5c</b> to also accept a combined session: exactly
/// one <c>clr-dump</c> artifact plus one <c>nettrace</c> artifact. Every other multi-kind shape is
/// still rejected — a session isn't yet designed to accept e.g. two dumps and a trace, or a dump
/// plus two traces, and accepting those silently would just move today's "how would the graph even
/// use this" question further downstream. The all-same-kind rule (trend/baseline dumps) is
/// unchanged.
/// </remarks>
public static class SessionValidator
{
    // Matches ArtifactDescriptor.SourceKind's own doc comment ("clr-dump", "nettrace", "gcdump",
    // ... — a source-neutral tag, not a file extension). No shared constant exists yet for these;
    // SingleDumpArtifactFactory/TraceArtifactFactory use the same literals.
    private const string ClrDumpSourceKind = "clr-dump";
    private const string NetTraceSourceKind = "nettrace";

    public static void Validate(IReadOnlyList<ArtifactDescriptor> artifacts)
    {
        if (artifacts.Count == 0)
            throw new SessionValidationException("A session needs at least one artifact.");

        string[] distinctKinds = artifacts.Select(a => a.SourceKind).Distinct(StringComparer.Ordinal).ToArray();
        if (distinctKinds.Length > 1 && !IsOneDumpPlusOneTrace(artifacts))
        {
            throw new SessionValidationException(
                "A session's artifacts must either all share one source kind, or be exactly one " +
                $"'{ClrDumpSourceKind}' artifact plus one '{NetTraceSourceKind}' artifact (a combined " +
                $"session); found source kinds: {string.Join(", ", distinctKinds)}.");
        }

        string[] duplicatePaths = artifacts
            .GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePaths.Length > 0)
            throw new SessionValidationException(
                $"A session's artifacts must be distinct paths; duplicated: {string.Join(", ", duplicatePaths)}.");
    }

    private static bool IsOneDumpPlusOneTrace(IReadOnlyList<ArtifactDescriptor> artifacts) =>
        artifacts.Count == 2
        && artifacts.Count(a => string.Equals(a.SourceKind, ClrDumpSourceKind, StringComparison.Ordinal)) == 1
        && artifacts.Count(a => string.Equals(a.SourceKind, NetTraceSourceKind, StringComparison.Ordinal)) == 1;
}

public sealed class SessionValidationException(string message) : Exception(message);
