using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectWcfChannelFault</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class WcfChannelFaultInsightRule : ISynthesisRule
{
    public string RuleId => "insight.wcf-channel-fault";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.wcf-channel-population", "runtime.exception-type"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
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
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long faultedChannels = (long)wcf.Measures["faulted.count"].Value;
        if (faultedChannels == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long commExCount = 0;
        var derivedFrom = new List<ObservationId> { wcf.Id };
        foreach (Observation exceptionType in exceptionTypes)
        {
            string typeName = ((TypeRef)exceptionType.Subjects[0]).CanonicalName;
            if (!typeName.StartsWith("System.ServiceModel.", StringComparison.Ordinal) &&
                typeName != "System.ObjectDisposedException")
                continue;

            commExCount += (long)exceptionType.Measures["instance.count"].Value;
            derivedFrom.Add(exceptionType.Id);
        }

        if (commExCount == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::wcf-channel-fault",
                Analyzer = "InsightEngine",
                Category = "Infrastructure",
                Severity = Severity.Critical,
                Title = "WCF faulted channels combined with communication exceptions",
                Narrative = $"{faultedChannels:N0} faulted WCF channel(s) on heap with " +
                            $"{commExCount:N0} WCF/communication exception(s). " +
                            "Calling any method on a faulted channel throws CommunicationObjectFaultedException.",
                Recommendation = "In the catch block for any WCF exception, call channel.Abort() instead of Close(). " +
                                 "Create a new channel per operation. Cache the ChannelFactory<T>, not the channel itself.",
                Tags = ["infrastructure", "wcf", "fault", "communication"],
                MetricValue = faultedChannels,
                MetricUnit = "faulted channels",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
