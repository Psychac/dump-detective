namespace DumpDetective.Analysis.Utilities;

/// <summary>
/// Sole owner of the GC Root analyzer's per-root severity judgment. Stage 1
/// (<c>GCRootAnalyzer</c>) emits only raw measures on <c>RootFinding</c> (retained bytes, root
/// kind); every consumer (section builder, trend comparer, and the analyzer's own ranking sort)
/// calls here instead of re-deriving its own scoring formula. See
/// docs/refactor/analyzer-pipeline-stages-and-leadfinding-dedup.md, Smell A.
/// </summary>
internal static class GCRootSeverityClassifier
{
    public static int ComputeSeverity(ulong retainedBytes, string kind)
    {
        // Base score retained size (log-scaled)
        int baseScore = retainedBytes switch
        {
            >= 100_000_000 => 100,
            >= 10_000_000 => 80,
            >= 1_000_000 => 60,
            >= 100_000 => 40,
            >= 10_000 => 20,
            _ => 5
        };

        // Kind multiplier: static/global roots are hardest to release
        int multiplier = kind switch
        {
            "StrongHandle" => 3,
            "FinalizerQueue" => 2,
            "PinnedHandle" => 2,
            "Stack" => 1,
            _ => 1
        };

        return Math.Min(baseScore * multiplier, 300);
    }
}
