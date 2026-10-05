using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>CrashFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Trusts <c>runtime.crash-thread-candidate</c> emission order for the "primary exception type"
/// text — <c>CrashAnalyzer</c> sorts <c>CrashThreadCandidates</c> descending by
/// <c>ActiveExceptionCount</c> before either the domain result or the observation loop is built,
/// the same already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies on. Falls
/// back to counting <c>runtime.active-exception</c> Subject types when no candidate observations
/// exist, matching the old generator's own fallback to <c>ActiveExceptionTypeCounts</c>.
/// <para>
/// The active-exceptions finding's confidence is the active-exception-weighted average of each
/// candidate's original-stack-trace inference tier, and its caveats summarize the tier
/// distribution — both as the old generator computed them. The tier travels as an
/// <c>original-stack-trace-confidence</c> <see cref="EvidenceRef"/> on each
/// <c>runtime.crash-thread-candidate</c>: it's a label, not a measure.
/// </para>
/// </remarks>
internal sealed class CrashSynthesisRule : ISynthesisRule
{
    private const string AnalyzerName = "Crash Analysis";
    private const string MeasuredActiveExceptionsCaveat = "Active exception count is based on measured runtime objects and thread snapshots.";
    private const double NoCandidateStrength = 0.5;

    public string RuleId => "crash.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-population", "runtime.active-exception", "runtime.crash-thread-candidate"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var candidates = new List<Observation>();
        var activeExceptionTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeExceptionIds = new List<ObservationId>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "runtime.crash-population":
                    population = observation;
                    break;
                case "runtime.crash-thread-candidate":
                    candidates.Add(observation);
                    break;
                case "runtime.active-exception":
                    activeExceptionIds.Add(observation.Id);
                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    activeExceptionTypeCounts.TryGetValue(typeName, out int count);
                    activeExceptionTypeCounts[typeName] = count + 1;
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalExceptions = (long)population.Measures["total.count"].Value;

        if (totalExceptions == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("no-exceptions"),
                Analyzer = AnalyzerName,
                Category = "Stability",
                Severity = Severity.Info,
                Title = "No exception objects detected",
                Narrative = "Crash analysis found no exception objects in the heap snapshot.",
                Recommendation = "Validate dump type and capture settings if a crash was expected.",
                Tags = ["crash", "exception", "stability"],
                MetricValue = 0,
                MetricUnit = "active-exceptions",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        long activeExceptions = (long)population.Measures["active.count"].Value;
        long uniqueTypes = (long)population.Measures["unique-types.count"].Value;

        if (activeExceptions > 0)
        {
            string topType;
            var derivedFrom = new List<ObservationId> { population.Id };

            Observation? topCandidate = candidates.Count > 0 ? candidates[0] : null;
            if (topCandidate is not null)
            {
                topType = ((TypeRef)topCandidate.Subjects[0]).CanonicalName;
                derivedFrom.Add(topCandidate.Id);
            }
            else if (activeExceptionTypeCounts.Count > 0)
            {
                string best = "";
                int bestCount = -1;
                foreach (KeyValuePair<string, int> kv in activeExceptionTypeCounts)
                {
                    if (kv.Value > bestCount)
                    {
                        bestCount = kv.Value;
                        best = kv.Key;
                    }
                }
                topType = best;
                derivedFrom.AddRange(activeExceptionIds);
            }
            else
            {
                topType = "Unknown";
            }

            string? tierSummary = SummarizeStackTraceTiers(candidates);

            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("active-exceptions"),
                Analyzer = AnalyzerName,
                Category = "Stability",
                Severity = Severity.Critical,
                Title = $"Active exceptions detected ({activeExceptions:N0} on thread stacks)",
                Narrative = $"{activeExceptions:N0} active exception(s) found on thread stacks. Primary type: {topType}. " +
                            $"Total exceptions: {totalExceptions:N0}; unique types: {uniqueTypes:N0}.",
                Recommendation = "Investigate the crash thread candidates; correlate with the thread section for full context.",
                Tags = ["crash", "exceptions", "threads"],
                MetricValue = activeExceptions,
                MetricUnit = "active-exceptions",
                DerivedFrom = derivedFrom,
                Confidence = StackTraceInferenceConfidence(candidates, tierSummary),
                Caveats = tierSummary is null
                    ? [MeasuredActiveExceptionsCaveat]
                    : [MeasuredActiveExceptionsCaveat, $"Original stack trace confidence: {tierSummary}."],
            }]);
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
        {
            Fingerprint = Fingerprint("exception-pressure"),
            Analyzer = AnalyzerName,
            Category = "Stability",
            Severity = Severity.Warning,
            Title = "Exception pressure in crash dump",
            Narrative = $"Total exceptions: {totalExceptions:N0}; active thread exceptions: 0; unique types: {uniqueTypes:N0}.",
            Recommendation = "Review top exception families for recurring fault paths.",
            Tags = ["crash", "exceptions", "threads"],
            MetricValue = 0,
            MetricUnit = "active-exceptions",
            DerivedFrom = [population.Id],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
        }]);
    }

    // Weighted by each candidate's active-exception count (at least 1), so the thread carrying most
    // of the exceptions dominates how far its original stack trace can be trusted.
    private static ConfidenceBreakdown StackTraceInferenceConfidence(IReadOnlyList<Observation> candidates, string? tierSummary)
    {
        double strength = NoCandidateStrength;
        if (candidates.Count > 0)
        {
            double weightedScore = 0;
            double totalWeight = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                double weight = Math.Max((int)candidates[i].Measures["active-exception.count"].Value, 1);
                weightedScore += StackTraceTierScore(StackTraceTier(candidates[i])) * weight;
                totalWeight += weight;
            }
            strength = totalWeight > 0 ? weightedScore / totalWeight : NoCandidateStrength;
        }

        return new ConfidenceBreakdown
        {
            Composite = strength,
            EvidenceStrength = strength,
            IdentityFidelityCap = 1.0,
            TemporalAlignmentCap = 1.0,
            CapabilityFidelity = 1.0,
            ConflictPenalty = 1.0,
            LimitingFactors = [tierSummary is null
                ? "No crash-thread candidate to attribute an original stack trace to."
                : $"Original stack trace confidence: {tierSummary}."],
        };
    }

    private static string StackTraceTier(Observation candidate) =>
        candidate.Evidence.Count > 0 ? candidate.Evidence[0].Description ?? "None" : "None";

    // Numeric anchors for CrashAnalyzer's inference tiers: Exact=High, ThreadId=Medium,
    // MessageHResult=Medium-Low, TypeInnerType=Low, None=no original trace found at all.
    private static double StackTraceTierScore(string tier) => tier switch
    {
        "Exact" => 0.95,
        "ThreadId" => 0.65,
        "MessageHResult" => 0.5,
        "TypeInnerType" => 0.3,
        "None" => 0.15,
        _ => 0.5,
    };

    private static string? SummarizeStackTraceTiers(IReadOnlyList<Observation> candidates)
    {
        if (candidates.Count == 0)
            return null;

        var tierCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Count; i++)
        {
            string tier = StackTraceTier(candidates[i]);
            tierCounts.TryGetValue(tier, out int count);
            tierCounts[tier] = count + 1;
        }

        return string.Join(", ", tierCounts
            .OrderByDescending(kvp => kvp.Value)
            .Select(kvp => $"{kvp.Value:N0} {kvp.Key}"));
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
