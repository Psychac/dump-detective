using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectWcfOpeningTimeoutCorrelation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class WcfOpeningTimeoutCorrelationInsightRule : ISynthesisRule
{
    public string RuleId => "insight.wcf-opening-timeout-correlation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.wcf-channel-population", "runtime.exception-type"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? wcf = null;
        var exceptionTypes = new List<Observation>();
        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.wcf-channel-population")
                wcf = observation;
            else if (observation.ObservationType == "runtime.exception-type")
                exceptionTypes.Add(observation);
        }

        if (wcf is null || wcf.Measures["total.count"].Value == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long openingChannels = (long)wcf.Measures["opening.count"].Value;
        if (openingChannels == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long timeoutCount = 0;
        var derivedFrom = new List<ObservationId> { wcf.Id };
        foreach (Observation exceptionType in exceptionTypes)
        {
            string typeName = ((TypeRef)exceptionType.Subjects[0]).CanonicalName;
            if (!IsTimeoutExceptionType(typeName))
                continue;

            timeoutCount += (long)exceptionType.Measures["instance.count"].Value;
            derivedFrom.Add(exceptionType.Id);
        }

        if (timeoutCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        Severity severity = openingChannels >= 5 || timeoutCount >= 10 ? Severity.Warning : Severity.Info;

        return ValueTask.FromResult<IReadOnlyList<Finding>>(
        [
            new Finding
            {
                Fingerprint = "insightengine::wcf-opening-timeout-correlation",
                Analyzer = "InsightEngine",
                Category = "Infrastructure",
                Severity = severity,
                Title = $"{openingChannels:N0} WCF channel(s) stuck Opening alongside timeout exceptions",
                Narrative = $"{openingChannels:N0} WCF channel(s) are in the Opening state with " +
                            $"{timeoutCount:N0} timeout/cancellation exception(s) on heap. " +
                            "A channel normally spends milliseconds in Opening — a channel captured in " +
                            "this state usually means Channel.Open()/OpenAsync() is blocked on a connect, " +
                            "DNS, or handshake timeout.",
                Recommendation = "Check the remote endpoint's availability and network path (firewall, DNS, " +
                                 "load balancer health). Confirm openTimeout is set appropriately for the " +
                                 "transport, and that Open()/OpenAsync() failures are observed and the channel " +
                                 "is Abort()ed rather than left half-open.",
                Tags = ["infrastructure", "wcf", "channel", "timeout", "connection"],
                MetricValue = openingChannels,
                MetricUnit = "opening channels",
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
