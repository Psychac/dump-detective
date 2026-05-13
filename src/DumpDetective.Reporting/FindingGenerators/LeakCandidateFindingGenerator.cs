using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class LeakCandidateFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "Leak Candidate Analysis";

    public bool CanGenerate(AnalyzerDomainResult result) => result is LeakCandidateDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not LeakCandidateDomainResult r || r.TopCandidates.Count == 0)
            return [];

        var findings = new List<InsightFinding>(capacity: Math.Min(3, r.TopCandidates.Count));
        int limit = Math.Min(3, r.TopCandidates.Count);

        for (int i = 0; i < limit; i++)
        {
            LeakCandidateRecord candidate = r.TopCandidates[i];
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Retention",
                Severity: candidate.Severity,
                Title: $"Leak candidate: {candidate.TypeName}",
                Evidence: $"{candidate.TypeName} scores {candidate.SuspicionScore:N0} with {FormatHelper.FormatBytes(candidate.TotalSize)} shallow size across {candidate.InstanceCount:N0} instances ({candidate.Gen2Pct:F1}% Gen2).",
                Recommendation: candidate.Classification switch
                {
                    LeakClass.StaticRetention => "Review static ownership and reduce singleton/stateful retention.",
                    LeakClass.EventLeak => "Unsubscribe long-lived publishers or switch to weak events.",
                    LeakClass.GCHandleRetention => "Free GC handles promptly and verify pinning scope.",
                    LeakClass.DependentHandleLeak => "Review ConditionalWeakTable ownership and cleanup.",
                    LeakClass.FinalizerRetention => "Prefer IDisposable and suppress finalization when possible.",
                    LeakClass.CacheLeak => "Bound cache size and add eviction.",
                    LeakClass.ThreadLocalLeak => "Dispose ThreadLocal<T> instances with thread lifetime.",
                    _ => "Inspect root paths and retention owners for this candidate."
                },
                Tags: ["leak", "candidate", candidate.Classification.ToString()],
                MetricValue: candidate.SuspicionScore,
                MetricUnit: "score"));
        }

        return findings;
    }
}