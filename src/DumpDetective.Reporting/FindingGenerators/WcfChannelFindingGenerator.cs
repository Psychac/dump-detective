using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class WcfChannelFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "WCF Channel Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is WcfChannelDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not WcfChannelDomainResult r || !r.WcfPresent) return [];

        var findings = new List<InsightFinding>(3);

        // ── Faulted channels finding (always actionable) ───────────────────────
        if (r.FaultedChannels > 0)
        {
            string typeBreakdown = BuildFaultedBreakdown(r.ByType);
            string endpointSummary = BuildEndpointSummary(r.TopFaultedChannels);
            WcfBindingHint dominantBinding = DominantFaultedBindingHint(r.ByType);

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Critical,
                Title: $"{r.FaultedChannels:N0} WCF channel(s) in Faulted state",
                Evidence: $"{r.FaultedChannels:N0} of {r.TotalChannels:N0} WCF channels are Faulted. " +
                          $"Types: {typeBreakdown}. {endpointSummary}" +
                          "A faulted channel cannot be reused and will throw CommunicationObjectFaultedException on all subsequent calls.",
                Recommendation:
                    "WCF best practice: wrap channel usage in try/catch. " +
                    "On success call channel.Close(). On any exception call channel.Abort(). " +
                    "Never call Close() on a faulted channel — it will throw. " +
                    "Faulted channels that are not Abort()ed retain server-side resources and may cause connection pool exhaustion. " +
                    BindingSpecificFaultGuidance(dominantBinding),
                Tags: ["infrastructure", "wcf", "channel", "fault"],
                MetricValue: r.FaultedChannels,
                MetricUnit: "faulted channels"));
        }

        // ── Channel count finding ─────────────────────────────────────────────
        if (r.TotalChannels >= 100)
        {
            FindingSeverity sev = r.TotalChannels >= 500 ? FindingSeverity.Critical : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.TotalChannels:N0} WCF channel objects on managed heap",
                Evidence: $"Total WCF channels: {r.TotalChannels:N0}. " +
                          $"Opening: {r.OpeningChannels:N0}, Opened: {r.OpenedChannels:N0}, Faulted: {r.FaultedChannels:N0}, " +
                          $"Closing: {r.ClosingChannels:N0}, Closed: {r.ClosedChannels:N0}, Other: {r.OtherChannels:N0}" +
                          (r.InvalidStateCount > 0 ? $", Invalid: {r.InvalidStateCount:N0}." : ".") +
                          $" Duplex-capable: {r.DuplexChannelCount:N0}, Session-based: {r.SessionChannelCount:N0}.",
                Recommendation:
                    "Each WCF channel holds a network connection and associated buffers. " +
                    "Create a new channel per logical operation and Close/Abort it immediately after use. " +
                    "Do not cache channels — cache the ChannelFactory<T> instead. " +
                    "Closed channels that are not collected indicate missing Dispose() or GC pressure.",
                Tags: ["infrastructure", "wcf", "channel", "leak"],
                MetricValue: r.TotalChannels,
                MetricUnit: "channels"));
        }

        // ── ChannelFactory detection ──────────────────────────────────────────
        if (r.FactoryCount > 0)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: $"{r.FactoryCount:N0} ChannelFactory<T> object(s) on managed heap",
                Evidence: $"{r.FactoryCount:N0} ChannelFactory instances found. Per-call ChannelFactory creation is a well-known expensive anti-pattern.",
                Recommendation:
                    "ChannelFactory<T> is expensive to create (DNS resolution, certificate negotiation, endpoint binding). " +
                    "Create a single static ChannelFactory<T> instance per service endpoint and reuse it to create channels. " +
                    "Presence of ChannelFactory objects on the heap indicates the application may be creating new factories per call.",
                Tags: ["infrastructure", "wcf", "factory", "performance"],
                MetricValue: r.FactoryCount,
                MetricUnit: "factories"));
        }

        return findings;
    }

    private static string BuildFaultedBreakdown(IReadOnlyList<WcfChannelTypeSummary> byType)
    {
        if (byType.Count == 0) return "(unknown)";
        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < byType.Count && shown < 3; i++)
        {
            if (byType[i].FaultedCount == 0) continue;
            if (shown > 0) sb.Append(", ");
            string shortName = byType[i].TypeName.Split('.')[^1];
            sb.Append($"{shortName} ×{byType[i].FaultedCount:N0}");
            shown++;
        }
        return sb.Length > 0 ? sb.ToString() : "(mixed types)";
    }

    private static WcfBindingHint DominantFaultedBindingHint(IReadOnlyList<WcfChannelTypeSummary> byType)
    {
        WcfBindingHint dominant = WcfBindingHint.Unknown;
        int dominantFaulted = 0;
        foreach (WcfChannelTypeSummary t in byType)
        {
            if (t.FaultedCount > dominantFaulted)
            {
                dominant = t.BindingHint;
                dominantFaulted = t.FaultedCount;
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

    private static string BuildEndpointSummary(IReadOnlyList<WcfChannelSnapshot> topFaulted)
    {
        if (topFaulted.Count == 0) return "";
        var endpoints = new System.Collections.Generic.HashSet<string>();
        foreach (var snap in topFaulted)
        {
            if (!string.IsNullOrEmpty(snap.RemoteAddress) && snap.RemoteAddress != "(unknown)")
                endpoints.Add(snap.RemoteAddress);
        }
        if (endpoints.Count == 0) return "";
        var sb = new System.Text.StringBuilder("Remote endpoints: ");
        int shown = 0;
        foreach (var ep in endpoints)
        {
            if (shown >= 3) break;
            if (shown > 0) sb.Append(", ");
            sb.Append(ep);
            shown++;
        }
        if (endpoints.Count > 3) sb.Append(", ...");
        sb.Append(". ");
        return sb.ToString();
    }
}
