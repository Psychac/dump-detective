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
}
