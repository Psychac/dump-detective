namespace DumpDetective.Analysis.Models;

/// <summary>
/// Shared "why is this alive / why does this matter" shape for leak-adjacent analyzers.
/// Consumed by the ranking engine and confidence scoring; produced only for a small
/// top-K set of items per analyzer, never for every candidate before filtering.
/// </summary>
internal sealed record Evidence(
    ulong EstimatedRetainedBytes,
    string? SampleRootPath,
    bool RootPathSearchTruncated,
    IReadOnlyList<EvidenceSignal> ContributingSignals);

internal readonly record struct EvidenceSignal(string Name, string Description, double? Value = null);

/// <summary>
/// EventLeak-specific evidence shape (design §4.3): the publisher's own BFS-derived root path
/// and a cheap subscriber-derived root hint are tracked separately rather than conflated into
/// one field, so the report can label which source produced the displayed path. Distinct from
/// the shared <see cref="Evidence"/> record used by Dominator/StaticRoot/Timer analyzers.
/// Kept here (not moved to <c>Plugins.Leaks</c> with the rest of the EventLeak domain model)
/// because <see cref="EvidenceConfidence"/>'s <c>EventLeakEvidence</c> overload, a shared,
/// source-neutral utility every domain's finding generator relies on, needs it directly and
/// must not depend on any <c>Plugins.*</c> package.
/// </summary>
internal sealed record EventLeakEvidence(
    int SchemaVersion,
    string? PublisherRootPath,
    string? SampleSubscriberHint,
    bool SearchTruncated,
    IReadOnlyList<EvidenceSignal> Signals);

/// <summary>
/// Derives a per-finding confidence score from <see cref="Evidence"/>: a resolved, non-truncated
/// sample root path is high confidence; a truncated path is partial/bounded; no path is speculative.
/// </summary>
internal static class EvidenceConfidence
{
    internal static double Compute(Evidence? evidence)
    {
        if (evidence is null || string.IsNullOrEmpty(evidence.SampleRootPath))
            return 0.4;

        double score = evidence.RootPathSearchTruncated ? 0.6 : 0.8;
        score += Math.Min(evidence.ContributingSignals.Count, 3) * 0.02;
        return Math.Min(score, 0.9);
    }

    /// <summary>
    /// EventLeak-specific overload for <see cref="EventLeakEvidence"/> (design §4.3).
    /// Only <c>PublisherRootPath</c> counts as a resolved path here — <c>SampleSubscriberHint</c>
    /// is a cheap fallback, not evidence the publisher itself is reachable from a root.
    /// </summary>
    internal static double Compute(EventLeakEvidence? evidence)
    {
        if (evidence is null || string.IsNullOrEmpty(evidence.PublisherRootPath))
            return 0.4;

        double score = evidence.SearchTruncated ? 0.6 : 0.8;
        score += Math.Min(evidence.Signals.Count, 3) * 0.02;
        return Math.Min(score, 0.9);
    }
}
