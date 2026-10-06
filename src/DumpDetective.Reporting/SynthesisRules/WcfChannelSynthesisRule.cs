using DumpDetective.Core.Enums;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>WcfChannelFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>FaultedChannels</c> is reconstructed by counting matched <c>infra.wcf-channel</c>
/// observations (the analyzer's own faulted-only stream) rather than duplicated as a measure.
/// The faulted-type breakdown trusts <c>infra.wcf-channel-type-population</c>'s emission order —
/// already sorted descending by <c>TotalCount</c> before the emission loop runs, the same
/// already-sorted-before-emission structure <c>ArraySynthesisRule</c> relies on — and derives each
/// type's <c>FaultedCount</c> by grouping the faulted-only stream by <c>Subjects[0]</c>'s canonical
/// name, not from a duplicated measure.
/// </remarks>
internal sealed class WcfChannelSynthesisRule : ISynthesisRule
{
    private const int MaxFaultedBreakdownEntries = 3;
    private const int MaxEndpointSummaryEntries = 3;
    private const int TotalChannelsWarningThreshold = 100;
    private const int TotalChannelsCriticalThreshold = 500;

    private const string AnalyzerName = "WCF Channel Analysis";

    public string RuleId => "wcf-channel.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.wcf-channel", "infra.wcf-channel-type-population", "infra.wcf-channel-population"],
    };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long faultedChannels = 0;
        var faultedIds = new List<ObservationId>();
        var faultedByType = new Dictionary<string, (long Count, WcfBindingHint BindingHint)>(StringComparer.Ordinal);
        var endpoints = new List<string>();
        var typePopulationOrder = new List<string>();

        foreach (Observation observation in matched.Matched)
        {
            switch (observation.ObservationType)
            {
                case "infra.wcf-channel":
                {
                    faultedChannels++;
                    faultedIds.Add(observation.Id);

                    string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
                    var bindingHint = (WcfBindingHint)(int)observation.Measures["binding-hint.ordinal"].Value;
                    faultedByType.TryGetValue(typeName, out var existing);
                    faultedByType[typeName] = (existing.Count + 1, bindingHint);

                    if (observation.Evidence.Count > 0 && observation.Evidence[0].Description is { Length: > 0 } remoteAddress)
                        endpoints.Add(remoteAddress);
                    break;
                }
                case "infra.wcf-channel-type-population":
                    typePopulationOrder.Add(((TypeRef)observation.Subjects[0]).CanonicalName);
                    break;
                case "infra.wcf-channel-population":
                    population = observation;
                    break;
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        IReadOnlyDictionary<string, Measure> m = population.Measures;
        long totalChannels = (long)m["total.count"].Value;
        long openingChannels = (long)m["opening.count"].Value;
        long openedChannels = (long)m["opened.count"].Value;
        long closingChannels = (long)m["closing.count"].Value;
        long closedChannels = (long)m["closed.count"].Value;
        long otherChannels = (long)m["other.count"].Value;
        long invalidStateCount = (long)m["invalid-state.count"].Value;
        long duplexChannelCount = (long)m["duplex.count"].Value;
        long sessionChannelCount = (long)m["session.count"].Value;
        long factoryCount = (long)m["factory.count"].Value;

        var findings = new List<Claim>(3);

        // ── Faulted channels finding (always actionable) ────────────────────────
        if (faultedChannels > 0)
        {
            string typeBreakdown = BuildFaultedBreakdown(typePopulationOrder, faultedByType);
            string endpointSummary = BuildEndpointSummary(endpoints);
            WcfBindingHint dominantBinding = DominantFaultedBindingHint(faultedByType);

            var derivedFrom = new List<ObservationId>(faultedIds) { population.Id };

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("faulted-channels"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Faults,
                    Measure = ClaimParts.Count(faultedChannels),
                    Qualifiers = new AssertionQualifiers { Location = "WCF channels in the Faulted state", Mechanism = "a faulted channel cannot be reused" },
                },
                Support =
                [
                    new EvidenceStatement(faultedIds, "Each is a WCF channel recorded in the Faulted state."),
                    new EvidenceStatement([population.Id], $"{faultedChannels:N0} of {totalChannels:N0} channel objects are faulted."),
                ],
                Counter = Counter.None("A channel seen faulted at capture time may already have been aborted and replaced; the dump does not show whether the owner handles faults."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -type ChannelBase",
                        Test = $"If the channel objects of the faulted types are not in the Faulted state when inspected with !do, the state reading is wrong.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the calling code aborts a channel on any exception and creates a fresh one, the faulted instances are awaiting collection, not in use.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Critical,
                Title = $"{faultedChannels:N0} WCF channel(s) in Faulted state",
                Narrative = $"{faultedChannels:N0} of {totalChannels:N0} WCF channels are Faulted. " +
                            $"Types: {typeBreakdown}. {endpointSummary}" +
                            "A faulted channel cannot be reused and will throw CommunicationObjectFaultedException on all subsequent calls.",
                Recommendation = "WCF best practice: wrap channel usage in try/catch. " +
                                  "On success call channel.Close(). On any exception call channel.Abort(). " +
                                  "Never call Close() on a faulted channel — it will throw. " +
                                  "Faulted channels that are not Abort()ed retain server-side resources and may cause connection pool exhaustion. " +
                                  BindingSpecificFaultGuidance(dominantBinding),
                Tags = ["infrastructure", "wcf", "channel", "fault"],
                MetricValue = faultedChannels,
                MetricUnit = "faulted channels",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── Channel count finding ─────────────────────────────────────────────────
        if (totalChannels >= TotalChannelsWarningThreshold)
        {
            Severity sev = totalChannels >= TotalChannelsCriticalThreshold ? Severity.Critical : Severity.Warning;

            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("channel-count"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exceeds,
                    Measure = ClaimParts.Count(totalChannels),
                    Reference = ClaimParts.Budget(
                        ClaimParts.Count(sev == Severity.Critical ? TotalChannelsCriticalThreshold : TotalChannelsWarningThreshold),
                        sev == Severity.Critical ? "critical threshold" : "warning threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "WCF channel objects on the managed heap" },
                },
                Support = [new EvidenceStatement([population.Id], $"{totalChannels:N0} channel objects: {openedChannels:N0} opened, {faultedChannels:N0} faulted, {closedChannels:N0} closed.")],
                Counter = closedChannels > openedChannels
                    ? Counter.Of(new EvidenceStatement([population.Id], $"{closedChannels:N0} channels are closed, more than the {openedChannels:N0} opened, so most are not holding a connection."))
                    : Counter.None("Channel objects on the heap include ones awaiting collection; nothing observed shows how long they have lived."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!dumpheap -live -stat -type ChannelBase",
                        Test = $"If far fewer than {totalChannels:N0} channel objects are live, the count includes garbage awaiting collection.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = sev,
                Title = $"{totalChannels:N0} WCF channel objects on managed heap",
                Narrative = $"Total WCF channels: {totalChannels:N0}. " +
                            $"Opening: {openingChannels:N0}, Opened: {openedChannels:N0}, Faulted: {faultedChannels:N0}, " +
                            $"Closing: {closingChannels:N0}, Closed: {closedChannels:N0}, Other: {otherChannels:N0}" +
                            (invalidStateCount > 0 ? $", Invalid: {invalidStateCount:N0}." : ".") +
                            $" Duplex-capable: {duplexChannelCount:N0}, Session-based: {sessionChannelCount:N0}.",
                Recommendation = "Each WCF channel holds a network connection and associated buffers. " +
                                  "Create a new channel per logical operation and Close/Abort it immediately after use. " +
                                  "Do not cache channels — cache the ChannelFactory<T> instead. " +
                                  "Closed channels that are not collected indicate missing Dispose() or GC pressure.",
                Tags = ["infrastructure", "wcf", "channel", "leak"],
                MetricValue = totalChannels,
                MetricUnit = "channels",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── ChannelFactory detection ──────────────────────────────────────────────
        if (factoryCount > 0)
        {
            findings.Add(new Claim
            {
                Fingerprint = Fingerprint("channel-factory"),
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(factoryCount),
                    Qualifiers = new AssertionQualifiers { Location = "ChannelFactory<T> objects on the managed heap" },
                },
                Support = [new EvidenceStatement([population.Id], $"{factoryCount:N0} ChannelFactory objects were found.")],
                Counter = factoryCount <= MaxExpectedFactories
                    ? Counter.Of(new EvidenceStatement([population.Id], $"Only {factoryCount:N0} factory object(s) exist; one factory per endpoint is the recommended pattern."))
                    : Counter.None("The count alone does not show factories are created per call; long-lived factories for several endpoints look the same."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Capture,
                        Test = "Capture a second dump under comparable load; if the same factory objects are present and the count does not grow, they are shared, not per-call.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If each factory is created once at startup and reused, its presence on the heap is by design.",
                    },
                ],
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{factoryCount:N0} ChannelFactory<T> object(s) on managed heap",
                Narrative = $"{factoryCount:N0} ChannelFactory instances found. Per-call ChannelFactory creation is a well-known expensive anti-pattern.",
                Recommendation = "ChannelFactory<T> is expensive to create (DNS resolution, certificate negotiation, endpoint binding). " +
                                  "Create a single static ChannelFactory<T> instance per service endpoint and reuse it to create channels. " +
                                  "Presence of ChannelFactory objects on the heap indicates the application may be creating new factories per call.",
                Tags = ["infrastructure", "wcf", "factory", "performance"],
                MetricValue = factoryCount,
                MetricUnit = "factories",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Claim>>(findings);
    }

    private static string BuildFaultedBreakdown(
        List<string> typePopulationOrder, Dictionary<string, (long Count, WcfBindingHint BindingHint)> faultedByType)
    {
        if (typePopulationOrder.Count == 0) return "(unknown)";

        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < typePopulationOrder.Count && shown < MaxFaultedBreakdownEntries; i++)
        {
            string typeName = typePopulationOrder[i];
            if (!faultedByType.TryGetValue(typeName, out var entry) || entry.Count == 0) continue;

            if (shown > 0) sb.Append(", ");
            string shortName = typeName.Split('.')[^1];
            sb.Append($"{shortName} ×{entry.Count:N0}");
            shown++;
        }
        return sb.Length > 0 ? sb.ToString() : "(mixed types)";
    }

    private static WcfBindingHint DominantFaultedBindingHint(Dictionary<string, (long Count, WcfBindingHint BindingHint)> faultedByType)
    {
        WcfBindingHint dominant = WcfBindingHint.Unknown;
        long dominantFaulted = 0;
        foreach ((long count, WcfBindingHint bindingHint) in faultedByType.Values)
        {
            if (count > dominantFaulted)
            {
                dominant = bindingHint;
                dominantFaulted = count;
            }
        }
        return dominant;
    }

    private static string BindingSpecificFaultGuidance(WcfBindingHint hint) => hint switch
    {
        WcfBindingHint.NetTcp =>
            "net.tcp channels most often fault from idle-connection timeouts or a mid-session TCP " +
            "reset — check receiveTimeout/reliableSession settings and confirm the server-side " +
            "connection quota isn't exhausted.",
        WcfBindingHint.NamedPipe =>
            "Named-pipe channels most often fault when the local host process serving the pipe " +
            "restarts or its ACL/session context changes — confirm the pipe server's process " +
            "lifetime matches client expectations.",
        WcfBindingHint.WsHttp =>
            "WS-* channels most often fault from an expired or renegotiated security token — check " +
            "token lifetime configuration and clock skew between client and server.",
        WcfBindingHint.Basic =>
            "basicHttp channels most often fault from an HTTP-level timeout or a 5xx server " +
            "response — check sendTimeout and server-side capacity.",
        _ => "Binding could not be determined from the channel type name.",
    };

    private static string BuildEndpointSummary(List<string> endpoints)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string endpoint in endpoints)
        {
            if (seen.Add(endpoint)) distinct.Add(endpoint);
        }
        if (distinct.Count == 0) return "";

        var sb = new System.Text.StringBuilder("Remote endpoints: ");
        int shown = 0;
        foreach (string endpoint in distinct)
        {
            if (shown >= MaxEndpointSummaryEntries) break;
            if (shown > 0) sb.Append(", ");
            sb.Append(endpoint);
            shown++;
        }
        if (distinct.Count > MaxEndpointSummaryEntries) sb.Append(", ...");
        sb.Append(". ");
        return sb.ToString();
    }

    private const int MaxExpectedFactories = 2;

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
