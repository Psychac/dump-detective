using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>EventLeakFindingGenerator</c> —
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Ports the old generator's <c>AddAggregateFinding</c> reduction loop directly onto
/// <c>event.publisher-leak-group</c> observations, filtered by the <c>is-static</c> measure — the
/// old generator doesn't sort or re-rank <c>TopLeakGroups</c> either, it's a single linear
/// max/first-two-distinct reduction, so no trust-emission-order question arises here.
/// <c>event.publisher-leak</c> (a different, top-50-by-subscriber-count sample from
/// <c>TopPublisherEvents</c>) is not part of this rule's query — it backs a different consumer, not
/// this finding generator.
/// <para>
/// Each aggregate finding's confidence comes from the sampled publisher root path of the
/// most-subscribed leak instance of that kind (<see cref="RootPathEvidenceStrength"/>), carried as
/// raw facts on <c>event.publisher-leak-population</c> — the same instance the old generator's
/// <c>EvidenceConfidence.Compute(topEvidence)</c> read.
/// </para>
/// </remarks>
internal sealed class EventLeakSynthesisRule : ISynthesisRule
{
    private const long SeverityCriticalThreshold = 35;
    private const long SeverityWarningThreshold = 20;

    private const string AnalyzerName = "Event Leak Analysis";

    public string RuleId => "event-leak.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["event.publisher-leak-group", "event.publisher-leak-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        var groups = new List<Observation>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "event.publisher-leak-population":
                    population = observation;
                    break;
                case "event.publisher-leak-group":
                    groups.Add(observation);
                    break;
            }
        }

        if (population is null)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long totalEventLeakInstances = (long)population.Measures["total.count"].Value;

        if (totalEventLeakInstances == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Claim>>([new Claim
            {
                Fingerprint = Fingerprint("no-signatures"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Normal,
                    Measure = ClaimParts.Count(0),
                    Qualifiers = new AssertionQualifiers { Location = "event publishers retaining subscribers" },
                },
                Support = [new EvidenceStatement([population.Id], "Counted no publisher instances with an event-leak signature.")],
                Counter = Counter.Of(new EvidenceStatement(
                    [population.Id],
                    "Only publishers over the subscriber thresholds are reported; smaller accumulation is outside this check.")),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -stat -type MulticastDelegate",
                        Test = "If a long-lived publisher's delegate field holds hundreds of subscribers, a leak was missed.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Leak",
                Severity = Severity.Info,
                Title = "No event-leak signatures detected",
                Narrative = "No significant event subscription patterns were detected.",
                Recommendation = "No immediate action required for event retention patterns.",
                Tags = ["event", "leak", "subscriptions"],
                MetricValue = 0,
                MetricUnit = "subscribers",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            }]);
        }

        long instanceLeakCount = (long)population.Measures["instance-leak.count"].Value;
        long staticLeakCount = (long)population.Measures["static-leak.count"].Value;

        var findings = new List<Claim>(2);

        Claim? instanceFinding = BuildAggregateFinding(groups, isStatic: false, instanceLeakCount, population);
        if (instanceFinding is not null) findings.Add(instanceFinding);

        Claim? staticFinding = BuildAggregateFinding(groups, isStatic: true, staticLeakCount, population);
        if (staticFinding is not null) findings.Add(staticFinding);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static Claim? BuildAggregateFinding(
        IReadOnlyList<Observation> groups, bool isStatic, long fallbackGroupCount, Observation population)
    {
        int groupCount = 0;
        long totalPublisherInstances = 0;
        long totalSubscribers = 0;
        long maxSubscribersPerInstance = 0;
        long maxSeverityScore = 0;

        string topPublisherType = string.Empty;
        string topEventFieldName = string.Empty;
        long topGroupSubscribers = -1;

        string exampleA = string.Empty;
        string exampleB = string.Empty;

        bool anyTimerEvent = false;
        bool anyPropertyChangedEvent = false;

        var contributingIds = new List<ObservationId>();

        foreach (Observation group in groups)
        {
            bool groupIsStatic = group.Measures["is-static"].Value >= 1.0;
            if (groupIsStatic != isStatic) continue;

            groupCount++;
            contributingIds.Add(group.Id);

            long instanceCount = (long)group.Measures["instance.count"].Value;
            long subscribers = (long)group.Measures["subscriber.count"].Value;
            long maxSubscribers = (long)group.Measures["max-subscribers-per-instance.count"].Value;
            long severityScore = (long)group.Measures["max-severity.score"].Value;

            totalPublisherInstances += instanceCount;
            totalSubscribers += subscribers;
            if (group.Measures["is-timer-event"].Value >= 1.0) anyTimerEvent = true;
            if (group.Measures["is-property-changed-event"].Value >= 1.0) anyPropertyChangedEvent = true;

            if (maxSubscribers > maxSubscribersPerInstance) maxSubscribersPerInstance = maxSubscribers;
            if (severityScore > maxSeverityScore) maxSeverityScore = severityScore;

            string publisherType = ((TypeRef)group.Subjects[0]).CanonicalName;
            string eventFieldName = group.Evidence.Count > 0 ? group.Evidence[0].Description ?? string.Empty : string.Empty;

            if (subscribers > topGroupSubscribers)
            {
                topGroupSubscribers = subscribers;
                topPublisherType = publisherType;
                topEventFieldName = eventFieldName;
            }

            string example = publisherType + "." + eventFieldName;
            if (string.IsNullOrEmpty(exampleA)) exampleA = example;
            else if (string.IsNullOrEmpty(exampleB) && !string.Equals(exampleA, example, StringComparison.Ordinal)) exampleB = example;
        }

        if (groupCount == 0)
            groupCount = (int)fallbackGroupCount;

        if (groupCount <= 0) return null;

        Severity severity = maxSeverityScore >= SeverityCriticalThreshold ? Severity.Critical
            : maxSeverityScore >= SeverityWarningThreshold ? Severity.Warning
            : Severity.Info;

        string leakKind = isStatic ? "static" : "instance";
        string title = groupCount == 1
            ? $"Potential {leakKind} event retention pattern detected"
            : $"Potential {leakKind} event retention patterns detected ({groupCount:N0})";

        string narrative = $"{groupCount:N0} leak group(s), {totalPublisherInstances:N0} publisher instance(s), {totalSubscribers:N0} total subscribers";
        if (maxSubscribersPerInstance > 0)
            narrative += $", max {maxSubscribersPerInstance:N0} per instance";

        if (!string.IsNullOrWhiteSpace(topPublisherType) && !string.IsNullOrWhiteSpace(topEventFieldName))
            narrative += $". Top group: {topPublisherType}.{topEventFieldName} ({topGroupSubscribers:N0} subscribers).";
        else
            narrative += ".";

        if (!string.IsNullOrEmpty(exampleA))
        {
            narrative += " Examples: " + exampleA;
            if (!string.IsNullOrEmpty(exampleB)) narrative += ", " + exampleB;
            narrative += ".";
        }

        string recommendation = "Ensure subscribers are unsubscribed and avoid long-lived static event publishers where possible.";
        if (anyTimerEvent)
            recommendation += " At least one leaking publisher is a Timer/DispatcherTimer — call Stop() and Dispose() when the owning object is done with it, not just unsubscribing the handler.";
        if (anyPropertyChangedEvent)
            recommendation += " At least one leaking publisher is an INotifyPropertyChanged.PropertyChanged event — verify view models unsubscribe on disposal/navigation-away.";

        var tags = new List<string>(capacity: 5) { "event-leak", isStatic ? "static-event" : "instance-event", "retention" };
        if (anyTimerEvent) tags.Add("timer-leak");
        if (anyPropertyChangedEvent) tags.Add("property-changed-leak");

        return new Claim
        {
            Fingerprint = Fingerprint(isStatic ? "static-retention" : "instance-retention"),
Assertion = new Assertion
{
    Predicate = AssertionPredicate.Leaks,
    Measure = ClaimParts.Count(totalSubscribers),
    Qualifiers = new AssertionQualifiers
    {
        Location = isStatic ? "subscribers held by static event publishers" : "subscribers held by instance event publishers",
        Mechanism = "event subscriptions",
    },
},
Support =
[
    new EvidenceStatement([population.Id], $"Counts {totalPublisherInstances:N0} publisher instance(s) with {totalSubscribers:N0} subscribers in {groupCount:N0} leak group(s)."),
    .. contributingIds.Count > 0
        ? [new EvidenceStatement(contributingIds, "Each is a group of publisher instances sharing a type and event field.")]
        : Array.Empty<EvidenceStatement>(),
],
Counter = Counter.None(isStatic
    ? "A static event keeps every subscriber alive until it unsubscribes; that is harmless only if the subscribers are meant to live as long as the process."
    : "An instance publisher retains its subscribers only while the publisher itself is alive; if the publishers are short-lived, so is the retention."),
FalsifiedBy =
[
    new FalsificationTest
    {
        Kind = FalsificationKind.Windbg,
        Command = "!gcroot <subscriber address>",
        Test = isStatic
            ? "On a sampled subscriber: if its path does not run through a static event field, it is not held by a static publisher."
            : "On a sampled subscriber: if its path runs only through a publisher that is itself unreachable, the subscribers are not retained.",
    },
    new FalsificationTest
    {
        Kind = FalsificationKind.Capture,
        Test = $"Capture a second dump under comparable load; if the {totalSubscribers:N0} subscribers do not grow, the retention is bounded, not accumulating.",
    },
],
            Analyzer = AnalyzerName,
            Category = "Leak",
            Severity = severity,
            Title = title,
            Narrative = narrative,
            Recommendation = recommendation,
            Tags = tags,
            MetricValue = totalSubscribers,
            MetricUnit = "subscribers",
            DerivedFrom = contributingIds.Count > 0 ? [population.Id, .. contributingIds] : [population.Id],
            Confidence = RootPathEvidenceStrength.FromMeasures(population.Measures, isStatic ? "top-static-publisher." : "top-instance-publisher."),
        };
    }


    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
