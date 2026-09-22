using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>LeakCandidateFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.1.
/// </summary>
/// <remarks>
/// Unlike every other rule in this batch, this one's inputs span four analyzers, not one:
/// <c>leak.candidate-type</c> (raw per-type facts, this analyzer's own new emission),
/// <c>handle.pinned-target-type</c>/<c>handle.dependent-target-type</c> (<c>GCHandleAnalyzer</c>,
/// also new — added specifically for this rule), and <c>infra.timer-population</c>
/// (<c>TimerLeakAnalyzer</c>, already existed). <c>Classify</c>/<c>Score</c>/<c>GetSeverity</c> are
/// moved in verbatim from <c>LeakCandidateAnalyzer</c> — byte-identical logic, just reading raw
/// measures off observations instead of <c>HeapTypeStatistics</c>/<c>LeakCandidateAnalyzerOptions</c>
/// fields directly. <c>LeakCandidateAnalyzer</c> itself still computes the same classification for
/// its own <c>LeakCandidateDomainResult</c> (unchanged) — <c>LeakAnalysisSectionBuilder</c> reads
/// that copy for section display, this rule reads its own copy for findings. This is the "benign
/// parallel selection" duplication the modularity audit doc's Smell A table already called out and
/// declined to flag; it's not new here.
/// </remarks>
internal sealed class LeakCandidateSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Leak Candidate Analysis";

    // Mirrors LeakCandidateAnalyzer's own private consts exactly — these are fixed generator
    // constants (not runtime-configurable via AnalyzerOptions the way GCHandleAnalysisOptions'
    // thresholds are), so hard-coding them here follows the same precedent as every other rule in
    // this batch, not GCHandleSynthesisRule's exception.
    private const int TimerLeakWarningThreshold = 100;
    private const int TimerLeakCriticalThreshold = 250;
    private const int CriticalCandidateLimit = 3;
    private const int AggregateConsideredLimit = 5;

    public string RuleId => "leak-candidate.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes =
        [
            "leak.candidate-type", "handle.pinned-target-type", "handle.dependent-target-type",
            "infra.timer-population",
        ],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        var pinnedTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        var dependentTargetTypes = new HashSet<string>(StringComparer.Ordinal);
        int logicalTimerCount = 0;
        var candidateObservations = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "leak.candidate-type":
                    candidateObservations.Add(observation);
                    break;
                case "handle.pinned-target-type":
                    pinnedTargetTypes.Add(((TypeRef)observation.Subjects[0]).CanonicalName);
                    break;
                case "handle.dependent-target-type":
                    dependentTargetTypes.Add(((TypeRef)observation.Subjects[0]).CanonicalName);
                    break;
                case "infra.timer-population":
                    logicalTimerCount = (int)observation.Measures["timer-queue.count"].Value;
                    break;
            }
        }

        if (candidateObservations.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        var candidates = new List<CandidateRow>(candidateObservations.Count);
        foreach (Observation observation in candidateObservations)
        {
            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
            IReadOnlyDictionary<string, Measure> m = observation.Measures;

            ulong totalSize = (ulong)m["shallow.bytes"].Value;
            long instanceCount = (long)m["instance.count"].Value;
            double gen2Pct = m["gen2.percent"].Value;
            long gen2Count = (long)m["gen2.count"].Value;
            double referenceFieldRatio = m["reference-field.ratio"].Value;
            bool isFinalizable = m["is-finalizable.flag"].Value != 0;
            bool isContainer = m["is-container.flag"].Value != 0;
            bool isStaticRooted = m["is-static-rooted.flag"].Value != 0;
            bool isDelegate = m["is-delegate.flag"].Value != 0;

            bool isPinned = pinnedTargetTypes.Contains(typeName);
            bool isDependent = dependentTargetTypes.Contains(typeName);

            LeakClass classification = Classify(
                typeName, isStaticRooted, isPinned, isDependent, isFinalizable, isDelegate, isContainer,
                gen2Pct, logicalTimerCount);

            int score = Score(
                totalSize, gen2Count, gen2Pct, isFinalizable, isStaticRooted, isPinned, isDependent,
                isContainer, referenceFieldRatio, typeName, isDelegate, logicalTimerCount);

            Severity severity = GetSeverity(score);

            candidates.Add(new CandidateRow(
                observation.Id, typeName, totalSize, instanceCount, gen2Pct, score, severity, classification));
        }

        // Same tie-break as LeakCandidateAnalyzer.Analyze's own candidates.Sort: score desc, then
        // size desc, then type name ordinal — reproduced here since observation match order carries
        // no ordering guarantee.
        candidates.Sort(static (a, b) =>
        {
            int score = b.Score.CompareTo(a.Score);
            if (score != 0)
                return score;

            int size = b.TotalSize.CompareTo(a.TotalSize);
            if (size != 0)
                return size;

            return StringComparer.Ordinal.Compare(a.TypeName, b.TypeName);
        });

        var findings = new List<Finding>();

        var criticalCandidates = new List<CandidateRow>();
        foreach (CandidateRow candidate in candidates)
        {
            if (candidate.Severity == Severity.Critical && criticalCandidates.Count < CriticalCandidateLimit)
                criticalCandidates.Add(candidate);
        }

        foreach (CandidateRow candidate in criticalCandidates)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint($"critical.{candidate.TypeName}"),
                Analyzer = AnalyzerName,
                Category = "Retention",
                Severity = Severity.Critical,
                Title = $"Critical leak candidate: {candidate.TypeName}",
                Narrative = $"Type: {candidate.TypeName}\n" +
                    $"Suspicion score: {candidate.Score:N0}\n" +
                    $"Shallow size: {FormatHelper.FormatBytes(candidate.TotalSize)} ({candidate.InstanceCount:N0} instances)\n" +
                    $"Generation: {candidate.Gen2Pct:F1}% Gen2\n" +
                    $"Classification: {candidate.Classification}",
                Recommendation = BuildRecommendation(candidate.Classification),
                Tags = ["leak", "candidate", "critical", candidate.Classification.ToString()],
                MetricValue = candidate.TotalSize,
                MetricUnit = "bytes",
                DerivedFrom = [candidate.ObservationId],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        if (findings.Count == 0)
        {
            int consideredCount = Math.Min(AggregateConsideredLimit, candidates.Count);
            long totalInstances = 0;
            ulong totalShallowBytes = 0;
            Severity maxSeverity = Severity.Info;
            LeakClass dominantClass = LeakClass.Unknown;
            int dominantClassCount = 0;

            string exampleA = string.Empty;
            string exampleB = string.Empty;
            string exampleC = string.Empty;

            var derivedFrom = new List<ObservationId>(consideredCount);
            var classCounts = new Dictionary<LeakClass, int>();
            for (int i = 0; i < consideredCount; i++)
            {
                CandidateRow candidate = candidates[i];
                totalShallowBytes += candidate.TotalSize;
                totalInstances += candidate.InstanceCount;
                derivedFrom.Add(candidate.ObservationId);

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

                string example = candidate.TypeName + " (score " + candidate.Score.ToString("N0") + ")";
                if (string.IsNullOrEmpty(exampleA)) exampleA = example;
                else if (string.IsNullOrEmpty(exampleB)) exampleB = example;
                else if (string.IsNullOrEmpty(exampleC)) exampleC = example;
            }

            string narrative =
                $"{consideredCount:N0} leak candidate(s) highlighted out of {candidates.Count:N0} total. " +
                $"Combined shallow size: {FormatHelper.FormatBytes(totalShallowBytes)} across {totalInstances:N0} instances.";

            if (!string.IsNullOrEmpty(exampleA))
            {
                narrative += " Top examples: " + exampleA;
                if (!string.IsNullOrEmpty(exampleB)) narrative += ", " + exampleB;
                if (!string.IsNullOrEmpty(exampleC)) narrative += ", " + exampleC;
                narrative += ".";
            }

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("aggregate"),
                Analyzer = AnalyzerName,
                Category = "Retention",
                Severity = maxSeverity,
                Title = consideredCount > 1
                    ? $"Leak candidate cluster detected ({consideredCount:N0} top candidates)"
                    : "Leak candidate detected",
                Narrative = narrative,
                Recommendation = BuildRecommendation(dominantClass),
                Tags = ["leak", "candidate", "aggregate", dominantClass.ToString()],
                MetricValue = totalShallowBytes,
                MetricUnit = "bytes",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private readonly record struct CandidateRow(
        ObservationId ObservationId,
        string TypeName,
        ulong TotalSize,
        long InstanceCount,
        double Gen2Pct,
        int Score,
        Severity Severity,
        LeakClass Classification);

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Critical => 3,
        Severity.Warning => 2,
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
        LeakClass.TimerLeak => "Dispose timers explicitly when no longer needed; avoid creating per-request or per-entity timers.",
        _ => "Inspect root paths and retention owners for top leak candidates."
    };

    // ── Moved verbatim from LeakCandidateAnalyzer (LeakClass enum lives in Core.Models — the
    // ── analyzer's own copy references it the same way) ────────────────────────────────────────

    private static LeakClass Classify(
        string typeName,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isFinalizable,
        bool isDelegate,
        bool isContainer,
        double gen2Pct,
        int logicalTimerCount)
    {
        if (typeName.Contains("ThreadLocal", StringComparison.OrdinalIgnoreCase))
            return LeakClass.ThreadLocalLeak;

        if (isDependent)
            return LeakClass.DependentHandleLeak;

        if (isPinned)
            return LeakClass.GCHandleRetention;

        if (isStaticRooted)
            return LeakClass.StaticRetention;

        if (logicalTimerCount >= TimerLeakWarningThreshold && TimerWrapperTypeNames.Names.Contains(typeName))
            return LeakClass.TimerLeak;

        if (isDelegate || typeName.Contains("Event", StringComparison.OrdinalIgnoreCase))
            return LeakClass.EventLeak;

        if (isFinalizable && gen2Pct > 50.0)
            return LeakClass.FinalizerRetention;

        if (isContainer && gen2Pct > 70.0)
            return LeakClass.CacheLeak;

        return LeakClass.Unknown;
    }

    private static int Score(
        ulong totalSize,
        long gen2Count,
        double gen2Pct,
        bool isFinalizable,
        bool isStaticRooted,
        bool isPinned,
        bool isDependent,
        bool isContainer,
        double referenceFieldRatio,
        string typeName,
        bool isDelegate,
        int logicalTimerCount)
    {
        int score = 0;

        if (gen2Pct > 80.0)
            score += 30;
        if (totalSize > 100UL * 1024 * 1024)
            score += 20;
        if (isFinalizable && gen2Count > 1000)
            score += 15;
        if (isStaticRooted)
            score += 10;
        if (isPinned)
            score += 10;
        if (isDependent)
            score += 10;
        if (isContainer)
            score += 5;
        if (referenceFieldRatio > 0.5)
            score += 5;
        if (isDelegate)
            score += 5;
        if (TimerWrapperTypeNames.Names.Contains(typeName))
        {
            if (logicalTimerCount >= TimerLeakCriticalThreshold)
                score += 20;
            else if (logicalTimerCount >= TimerLeakWarningThreshold)
                score += 10;
        }

        return score;
    }

    private static Severity GetSeverity(int score)
        => score >= 90
            ? Severity.Critical
            : score >= 70
                ? Severity.Warning
                : Severity.Info;
}
