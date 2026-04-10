namespace DumpDetective.Models
{
    internal sealed record AnalysisSnapshot(
        int Index,
        string DumpPath,
        IReadOnlyList<InsightFinding> Findings,
        DateTime GeneratedAtUtc);
}
