namespace DumpDetective.Core.Options;

public sealed class HangAnalysisOptions
{
    // Semantic threshold (Category 5, §3.1 D4) — shakiest of the flagged thresholds in this doc
    // (ignores machine core count/workload), kept as the single Balanced value pending field data.
    public int HighThreadPoolThreshold { get; init; } = 100;
}
