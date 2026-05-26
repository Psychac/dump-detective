using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class WcfChannelFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "WCF Channel Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is WcfChannelDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not WcfChannelDomainResult r || !r.WcfPresent) return [];

        var findings = new List<InsightFinding>(2);

        // ── Faulted channels finding (always actionable) ───────────────────────
        if (r.FaultedChannels > 0)
        {
            string typeBreakdown = BuildFaultedBreakdown(r.ByType);

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Critical,
                Title: $"{r.FaultedChannels:N0} WCF channel(s) in Faulted state",
                Evidence: $"{r.FaultedChannels:N0} of {r.TotalChannels:N0} WCF channels are Faulted. " +
                          $"Types: {typeBreakdown}. " +
                          "A faulted channel cannot be reused and will throw CommunicationObjectFaultedException on all subsequent calls.",
                Recommendation:
                    "WCF best practice: wrap channel usage in try/catch. " +
                    "On success call channel.Close(). On any exception call channel.Abort(). " +
                    "Never call Close() on a faulted channel — it will throw. " +
                    "Faulted channels that are not Abort()ed retain server-side resources and may cause connection pool exhaustion.",
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
                          $"Opened: {r.OpenedChannels:N0}, Faulted: {r.FaultedChannels:N0}, " +
                          $"Closed: {r.ClosedChannels:N0}, Other: {r.OtherChannels:N0}.",
                Recommendation:
                    "Each WCF channel holds a network connection and associated buffers. " +
                    "Create a new channel per logical operation and Close/Abort it immediately after use. " +
                    "Do not cache channels — cache the ChannelFactory<T> instead. " +
                    "Closed channels that are not collected indicate missing Dispose() or GC pressure.",
                Tags: ["infrastructure", "wcf", "channel", "leak"],
                MetricValue: r.TotalChannels,
                MetricUnit: "channels"));
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
}
