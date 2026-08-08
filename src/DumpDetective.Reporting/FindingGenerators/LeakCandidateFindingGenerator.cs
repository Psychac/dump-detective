using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class LeakCandidateFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Leak Candidate Analysis";

    public bool CanGenerate(AnalyzerDomainResult result) => result is LeakCandidateDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not LeakCandidateDomainResult r || r.TopCandidates.Count == 0)
            return [];

        var findings = new List<InsightFinding>();

        var criticalCandidates = new List<LeakCandidateRecord>();
        int criticalLimit = 3;
        foreach (LeakCandidateRecord candidate in r.TopCandidates)
        {
            if (candidate.Severity == FindingSeverity.Critical && criticalCandidates.Count < criticalLimit)
                criticalCandidates.Add(candidate);
        }

        foreach (LeakCandidateRecord candidate in criticalCandidates)
        {
            var finding = new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: FindingSeverity.Critical,
                Title: $"Critical leak candidate: {candidate.TypeName}",
                Evidence: $"Type: {candidate.TypeName}\n" +
                    $"Suspicion score: {candidate.SuspicionScore:N0}\n" +
                    $"Shallow size: {FormatHelper.FormatBytes(candidate.TotalSize)} ({candidate.InstanceCount:N0} instances)\n" +
                    $"Generation: {candidate.Gen2Pct:F1}% Gen2\n" +
                    $"Classification: {candidate.Classification}",
                Recommendation: BuildRecommendation(candidate.Classification),
                Tags: ["leak", "candidate", "critical", candidate.Classification.ToString()],
                MetricValue: candidate.TotalSize,
                MetricUnit: "bytes");
            findings.Add(finding);
        }

        if (findings.Count == 0)
        {
            int consideredCount = Math.Min(5, r.TopCandidates.Count);
            long totalInstances = 0;
            ulong totalShallowBytes = 0;
            FindingSeverity maxSeverity = FindingSeverity.Info;
            LeakClass dominantClass = LeakClass.Unknown;
            int dominantClassCount = 0;

            string exampleA = string.Empty;
            string exampleB = string.Empty;
            string exampleC = string.Empty;

            var classCounts = new Dictionary<LeakClass, int>();
            for (int i = 0; i < consideredCount; i++)
            {
                LeakCandidateRecord candidate = r.TopCandidates[i];
                totalShallowBytes += candidate.TotalSize;
                totalInstances += candidate.InstanceCount;

                if (SeverityRank(candidate.Severity) > SeverityRank(maxSeverity))
                    maxSeverity = candidate.Severity;

                if (!classCounts.TryGetValue(candidate.Classification, out int classCount))
                    classCount = 0;
                classCount++;
                classCounts[candidate.Classification] = classCount;

                if (classCount > dominantClassCount)
                {
                    dominantClass = candidate.Classification;
                    dominantClassCount = classCount;
                }

                string example = candidate.TypeName + " (score " + candidate.SuspicionScore.ToString("N0") + ")";
                if (string.IsNullOrEmpty(exampleA)) exampleA = example;
                else if (string.IsNullOrEmpty(exampleB)) exampleB = example;
                else if (string.IsNullOrEmpty(exampleC)) exampleC = example;
            }

            string evidence =
                $"{consideredCount:N0} leak candidate(s) highlighted out of {r.TotalCandidates:N0} total. " +
                $"Combined shallow size: {FormatHelper.FormatBytes(totalShallowBytes)} across {totalInstances:N0} instances.";

            if (!string.IsNullOrEmpty(exampleA))
            {
                evidence += " Top examples: " + exampleA;
                if (!string.IsNullOrEmpty(exampleB)) evidence += ", " + exampleB;
                if (!string.IsNullOrEmpty(exampleC)) evidence += ", " + exampleC;
                evidence += ".";
            }

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: maxSeverity,
                Title: consideredCount > 1
                    ? $"Leak candidate cluster detected ({consideredCount:N0} top candidates)"
                    : "Leak candidate detected",
                Evidence: evidence,
                Recommendation: BuildRecommendation(dominantClass),
                Tags: ["leak", "candidate", "aggregate", dominantClass.ToString()],
                MetricValue: totalShallowBytes,
                MetricUnit: "bytes"));
        }

        return findings;
    }

    private static int SeverityRank(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 3,
        FindingSeverity.Warning => 2,
        _ => 1
    };

    private static string BuildRecommendation(LeakClass classification) => classification switch
    {
        LeakClass.StaticRetention => "Review static ownership and reduce singleton/stateful retention.",
        LeakClass.EventLeak => "Unsubscribe long-lived publishers or switch to weak events.",
        LeakClass.GCHandleRetention => "Free GC handles promptly and verify pinning scope.",
        LeakClass.DependentHandleLeak => "Review ConditionalWeakTable ownership and cleanup.",
        LeakClass.FinalizerRetention => "Prefer IDisposable and suppress finalization when possible.",
        LeakClass.CacheLeak => "Bound cache size and add eviction.",
        LeakClass.ThreadLocalLeak => "Dispose ThreadLocal<T> instances with thread lifetime.",
        _ => "Inspect root paths and retention owners for top leak candidates."
    };
}