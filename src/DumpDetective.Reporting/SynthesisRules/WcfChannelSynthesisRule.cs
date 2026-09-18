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

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
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

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

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

        var findings = new List<Finding>(3);

        // ── Faulted channels finding (always actionable) ────────────────────────
        if (faultedChannels > 0)
        {
            string typeBreakdown = BuildFaultedBreakdown(typePopulationOrder, faultedByType);
            string endpointSummary = BuildEndpointSummary(endpoints);
            WcfBindingHint dominantBinding = DominantFaultedBindingHint(faultedByType);

            var derivedFrom = new List<ObservationId>(faultedIds) { population.Id };

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("faulted-channels"),
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

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("channel-count"),
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
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("channel-factory"),
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

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
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

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
