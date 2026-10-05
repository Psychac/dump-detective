using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectRecurringTimeoutPattern</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class RecurringTimeoutPatternInsightRule : ISynthesisRule
{
    public string RuleId => "insight.recurring-timeout-pattern";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["runtime.crash-population", "runtime.exception-type"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var exceptionTypes = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "runtime.crash-population")
                population = observation;
            else if (observation.ObservationType == "runtime.exception-type")
                exceptionTypes.Add(observation);
        }

        long totalExceptions = (long)(population?.Measures["total.count"].Value ?? 0);
        if (population is null || totalExceptions == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long timeoutCount = 0;
        long objectDisposedCount = 0;
        var derivedFrom = new List<ObservationId> { population.Id };

        foreach (Observation exceptionType in exceptionTypes)
        {
            string typeName = ((TypeRef)exceptionType.Subjects[0]).CanonicalName;
            long count = (long)exceptionType.Measures["instance.count"].Value;

            bool relevant = false;
            if (IsTimeoutExceptionType(typeName)) { timeoutCount += count; relevant = true; }
            if (typeName == "System.ObjectDisposedException") { objectDisposedCount += count; relevant = true; }

            if (relevant) derivedFrom.Add(exceptionType.Id);
        }

        if (timeoutCount < 10)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        string disposedNote = objectDisposedCount >= 5
            ? $" Combined with {objectDisposedCount:N0} ObjectDisposedException(s), this may indicate connections " +
              "being used after pool exhaustion or channel faults."
            : string.Empty;

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::recurring-timeout-pattern",
                Analyzer = "InsightEngine",
                Category = "Exceptions",
                Severity = timeoutCount >= 20 ? Severity.Warning : Severity.Info,
                Title = "Recurring timeout exceptions indicate dependency instability",
                Narrative = $"{timeoutCount:N0} timeout/cancellation exception(s) on heap.{disposedNote}",
                Recommendation = "Review connection pool sizing, query/call timeouts, and retry policies. " +
                                 "High timeout counts often indicate: (1) DB connection pool exhaustion, " +
                                 "(2) downstream service latency, or (3) network intermittency. " +
                                 "Correlate with DB connections on heap and WCF channel state.",
                Tags = ["timeout", "exceptions", "connection-pool", "performance"],
                MetricValue = timeoutCount,
                MetricUnit = "exceptions",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }

    private static bool IsTimeoutExceptionType(string typeName) =>
        typeName is "System.TimeoutException" or
            "System.OperationCanceledException" or
            "System.Net.WebException" ||
        typeName.EndsWith("TimeoutException", StringComparison.OrdinalIgnoreCase);
}
