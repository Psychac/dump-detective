using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.FindingGenerators;

internal sealed class HttpObjectFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "HTTP Object Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is HttpObjectDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not HttpObjectDomainResult r || !r.HttpObjectsFound) return [];

        var findings = new List<InsightFinding>(3);

        // ── HttpClient misuse ──────────────────────────────────────────────────
        if (r.HttpClientCount >= 5)
        {
            FindingSeverity sev = r.HttpClientCount >= 20 ? FindingSeverity.Critical : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: sev,
                Title: $"{r.HttpClientCount:N0} HttpClient instances on managed heap",
                Evidence: $"{r.HttpClientCount:N0} System.Net.Http.HttpClient instances found. " +
                          "HttpClient is designed for long-lived reuse; creating per-request instances " +
                          "exhausts ephemeral TCP ports (TIME_WAIT) and can cause connection failures " +
                          "under load even before garbage collection.",
                Recommendation:
                    "Use IHttpClientFactory (ASP.NET Core) or a static/singleton HttpClient. " +
                    "HttpClientFactory manages handler lifetime and DNS refresh automatically. " +
                    "If pre-.NET Core: create one HttpClient per base URI and reuse it.",
                Tags: ["infrastructure", "http", "httpclient", "sockets"],
                MetricValue: r.HttpClientCount,
                MetricUnit: "HttpClient instances"));
        }

        // ── HttpWebResponse not disposed ──────────────────────────────────────
        if (r.HttpWebResponseCount >= 20)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: $"{r.HttpWebResponseCount:N0} HttpWebResponse objects on managed heap",
                Evidence: $"{r.HttpWebResponseCount:N0} System.Net.HttpWebResponse objects found. " +
                          "Each live HttpWebResponse holds the underlying network stream open until disposed. " +
                          "Undisposed responses exhaust connection pool slots.",
                Recommendation:
                    "Always dispose HttpWebResponse: 'using var response = (HttpWebResponse)request.GetResponse()'. " +
                    "Prefer HttpClient over HttpWebRequest for new code.",
                Tags: ["infrastructure", "http", "httpwebresponse", "dispose"],
                MetricValue: r.HttpWebResponseCount,
                MetricUnit: "HttpWebResponse objects"));
        }

        // ── ServicePoint accumulation ─────────────────────────────────────────
        if (r.ServicePointCount >= 50)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: $"{r.ServicePointCount:N0} ServicePoint objects on managed heap",
                Evidence: $"{r.ServicePointCount:N0} System.Net.ServicePoint objects found. " +
                          "ServicePoint objects accumulate when many distinct remote endpoints are called. " +
                          "ServicePointManager.MaxServicePoints defaults to 0 (unlimited), which can cause OOM.",
                Recommendation:
                    "Set ServicePointManager.MaxServicePoints to a reasonable bound (e.g. 100). " +
                    "ServicePoint is obsolete in .NET 6+; prefer HttpClient/SocketsHttpHandler. " +
                    "Investigate whether the application is calling many distinct hostnames.",
                Tags: ["infrastructure", "http", "servicepoint", "sockets"],
                MetricValue: r.ServicePointCount,
                MetricUnit: "ServicePoint objects"));
        }

        return findings;
    }
}
