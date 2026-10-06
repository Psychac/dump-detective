using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Utilities;
using DumpDetective.Core.Enums;
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
/// (<c>TimerLeakAnalyzer</c>, already existed). The judgment itself is
/// <see cref="LeakCandidateClassifier"/>, shared with the analyzer's ranking, its section builder,
/// and its trend comparer — this rule just feeds it raw measures read off observations.
/// </remarks>
internal sealed class LeakCandidateSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Leak Candidate Analysis";

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

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

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

            LeakCandidateAssessment assessment = LeakCandidateClassifier.Assess(
                typeName, totalSize, gen2Count, gen2Pct, isFinalizable, isStaticRooted, isPinned, isDependent,
                isContainer, referenceFieldRatio, isDelegate, logicalTimerCount);

            candidates.Add(new CandidateRow(
                observation.Id, typeName, totalSize, instanceCount, gen2Pct, assessment.SuspicionScore,
                ToSeverity(assessment.Severity), assessment.Classification));
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

        var findings = new List<Claim>();

        var criticalCandidates = new List<CandidateRow>();
        foreach (CandidateRow candidate in candidates)
        {
            if (candidate.Severity == Severity.Critical && criticalCandidates.Count < CriticalCandidateLimit)
                criticalCandidates.Add(candidate);
        }

        foreach (CandidateRow candidate in criticalCandidates)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint($"critical.{candidate.TypeName}"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Leaks,
                    Measure = ClaimParts.Bytes(candidate.TotalSize),
                    Qualifiers = new AssertionQualifiers { Location = $"instances of {candidate.TypeName}", Mechanism = candidate.Classification.ToString() },
                },
                Support = [new EvidenceStatement([candidate.ObservationId], $"{candidate.TypeName} scores {candidate.Score:N0}, classified {candidate.Classification}: {candidate.InstanceCount:N0} instances, {candidate.Gen2Pct:F1}% in Gen2.")],
                Counter = HeuristicCounter(candidate.ObservationId),
                FalsifiedBy = LeakFalsifiedBy(candidate.TypeName),
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
                Confidence = HeuristicClassificationConfidence,
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

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("aggregate"),
                Assertion = new Assertion
                {
                    Predicate = maxSeverity == Severity.Info ? AssertionPredicate.Exhibits : AssertionPredicate.Leaks,
                    Measure = ClaimParts.Bytes(totalShallowBytes),
                    Qualifiers = new AssertionQualifiers { Location = $"the top {consideredCount} leak candidate type(s)", Mechanism = dominantClass.ToString() },
                },
                Support = [new EvidenceStatement(derivedFrom, $"{consideredCount:N0} candidate type(s) of {candidates.Count:N0} hold {FormatHelper.FormatBytes(totalShallowBytes)} across {totalInstances:N0} instances.")],
                Counter = HeuristicCounter(derivedFrom[0]),
                FalsifiedBy = LeakFalsifiedBy(exampleA),
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
                Confidence = HeuristicClassificationConfidence,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
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

    // Suspicion scoring and classification are heuristics over per-type shape facts — no full
    // retention scan proves a candidate leaks — so every finding from them carries that ceiling.
    private static readonly ConfidenceBreakdown HeuristicClassificationConfidence = DataQualityPenalties.Apply(
        ConfidenceBreakdown.MeasurementCertain with { Composite = 0.75, EvidenceStrength = 0.75 },
        new DataQualityPenalty(true, 0.15, "Heuristic-only leak analysis; no full retention scan."));

    private static Counter HeuristicCounter(ObservationId candidateObservation) =>
        Counter.Of(new EvidenceStatement(
            [candidateObservation],
            "Suspicion scoring and classification are heuristics over per-type shape facts; no full retention scan shows this type is leaked."));

    private static IReadOnlyList<FalsificationTest> LeakFalsifiedBy(string typeDescription) =>
    [
        new FalsificationTest
        {
            Kind = FalsificationKind.Windbg,
            Command = "!gcroot <instance address>",
            Test = $"On a sampled instance of {typeDescription}: if its root is a request frame, the instances are short-lived, not retained.",
        },
        new FalsificationTest
        {
            Kind = FalsificationKind.Capture,
            Test = "Capture a second dump under comparable load; if the instance count and size do not grow, the type is a stable working set, not a leak.",
        },
    ];

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

    private static Severity ToSeverity(Severity severity) => severity switch
    {
        Severity.Critical => Severity.Critical,
        Severity.Warning => Severity.Warning,
        _ => Severity.Info,
    };
}
