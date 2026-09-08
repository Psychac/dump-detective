using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class CrashFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Crash Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is CrashDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not CrashDomainResult r) return [];

        if (r.TotalExceptions == 0)
        {
            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Stability",
                    Severity: FindingSeverity.Info,
                    Title: "No exception objects detected",
                    Evidence: "Crash analysis found no exception objects in the heap snapshot.",
                    Recommendation: "Validate dump type and capture settings if a crash was expected.",
                    Tags: ["crash", "exception", "stability"],
                    MetricValue: 0,
                    MetricUnit: "active-exceptions")
            ];
        }

        if (r.ActiveExceptions > 0)
        {
            string topType = r.TopCrashThreadCandidates is { Count: > 0 }
                ? r.TopCrashThreadCandidates[0].PrimaryExceptionType
                : (r.ActiveExceptionTypeCounts.Count > 0
                    ? r.ActiveExceptionTypeCounts.OrderByDescending(kvp => kvp.Value).First().Key
                    : "Unknown");

            double confidenceScore = ComputeCandidateConfidence(r.TopCrashThreadCandidates);
            var caveats = new List<string> { "Active exception count is based on measured runtime objects and thread snapshots." };
            string? tierSummary = SummarizeConfidenceTiers(r.TopCrashThreadCandidates);
            if (tierSummary != null)
                caveats.Add($"Original stack trace confidence: {tierSummary}.");

            return
            [
                new InsightFinding(
                    Analyzer: AnalyzerName,
                    Category: "Stability",
                    Severity: FindingSeverity.Critical,
                    Title: $"Active exceptions detected ({r.ActiveExceptions:N0} on thread stacks)",
                    Evidence: $"{r.ActiveExceptions:N0} active exception(s) found on thread stacks. Primary type: {topType}. " +
                              $"Total exceptions: {r.TotalExceptions:N0}; unique types: {r.ExceptionTypeCounts.Count:N0}.",
                    Recommendation: "Investigate the crash thread candidates; correlate with the thread section for full context.",
                    Tags: ["crash", "exceptions", "threads"],
                    MetricValue: r.ActiveExceptions,
                    MetricUnit: "active-exceptions",
                    ConfidenceScore: confidenceScore,
                    Caveats: caveats)
            ];
        }

        return
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Stability",
                Severity: FindingSeverity.Warning,
                Title: "Exception pressure in crash dump",
                Evidence: $"Total exceptions: {r.TotalExceptions:N0}; active thread exceptions: 0; unique types: {r.ExceptionTypeCounts.Count:N0}.",
                Recommendation: "Review top exception families for recurring fault paths.",
                Tags: ["crash", "exceptions", "threads"],
                MetricValue: 0,
                MetricUnit: "active-exceptions")
        ];
    }

    // Weighted (by ActiveExceptionCount) average of per-candidate tier scores, so a handful of
    // Exact-confidence threads with many active exceptions outweigh a single low-confidence
    // outlier, and vice versa. Falls back to a neutral 0.5 when there are no candidates.
    private static double ComputeCandidateConfidence(IReadOnlyList<CrashThreadCandidateSnapshot>? candidates)
    {
        if (candidates is not { Count: > 0 })
            return 0.5;

        double weightedScore = 0;
        double totalWeight = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            CrashThreadCandidateSnapshot candidate = candidates[i];
            double weight = Math.Max(candidate.ActiveExceptionCount, 1);
            weightedScore += ConfidenceTierScore(candidate.OriginalStackTraceConfidence) * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedScore / totalWeight : 0.5;
    }

    // Numeric anchors matching the qualitative tiers: Exact=High, ThreadId=Medium,
    // MessageHResult=Medium-Low, TypeInnerType=Low, None=no original trace found at all.
    private static double ConfidenceTierScore(InferenceConfidence confidence) => confidence switch
    {
        InferenceConfidence.Exact => 0.95,
        InferenceConfidence.ThreadId => 0.65,
        InferenceConfidence.MessageHResult => 0.5,
        InferenceConfidence.TypeInnerType => 0.3,
        InferenceConfidence.None => 0.15,
        _ => 0.5,
    };

    private static string? SummarizeConfidenceTiers(IReadOnlyList<CrashThreadCandidateSnapshot>? candidates)
    {
        if (candidates is not { Count: > 0 })
            return null;

        var tierCounts = new Dictionary<InferenceConfidence, int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            InferenceConfidence tier = candidates[i].OriginalStackTraceConfidence;
            tierCounts.TryGetValue(tier, out int count);
            tierCounts[tier] = count + 1;
        }

        return string.Join(", ", tierCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => $"{kvp.Value:N0} {kvp.Key}"));
    }
}
