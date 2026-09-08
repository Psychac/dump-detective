namespace DumpDetective.Reporting.Services;

/// <summary>
/// Shared confidence-band computation for section builders. Starts from an analyzer's
/// inherent measurement-vs-heuristic base score and applies penalties for active
/// scan-quality flags (capped scans, skipped reference counting, heuristic-only mode).
/// </summary>
internal static class ConfidenceScoring
{
    internal readonly record struct Flag(bool Active, double Penalty, string Caveat);

    internal static Flag F(bool active, double penalty, string caveat) => new(active, penalty, caveat);

    internal static (double Score, IReadOnlyList<string> Caveats) Compute(double baseScore, params Flag[] flags)
    {
        double score = baseScore;
        List<string>? caveats = null;
        foreach (Flag flag in flags)
        {
            if (!flag.Active)
                continue;

            score -= flag.Penalty;
            caveats ??= new List<string>();
            caveats.Add(flag.Caveat);
        }

        score = Math.Clamp(score, 0.2, 1.0);
        return (score, (IReadOnlyList<string>?)caveats ?? Array.Empty<string>());
    }
}
