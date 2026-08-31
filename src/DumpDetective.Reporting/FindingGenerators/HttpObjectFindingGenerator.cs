using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

namespace DumpDetective.Reporting.FindingGenerators;

internal sealed class HttpObjectFindingGenerator : IFindingGenerator
{
    public string AnalyzerName => "HTTP Object Analysis";
    public bool CanGenerate(AnalyzerDomainResult result) => result is HttpObjectDomainResult;

    public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result)
    {
        if (result is not HttpObjectDomainResult r || !r.HttpObjectsFound) return [];

        var findings = new List<InsightFinding>(4);

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
                          "under load even before garbage collection." +
                          BuildGenerationEvidence(r),
                Recommendation:
                    "Use IHttpClientFactory (ASP.NET Core) or a static/singleton HttpClient. " +
                    "HttpClientFactory manages handler lifetime and DNS refresh automatically. " +
                    "If pre-.NET Core: create one HttpClient per base URI and reuse it.",
                Tags: ["infrastructure", "http", "httpclient", "sockets"],
                MetricValue: r.HttpClientCount,
                MetricUnit: "HttpClient instances"));
        }

        // ── HttpWebRequest accumulation (obsolete API) ────────────────────────
        if (r.HttpWebRequestCount >= 10)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: $"{r.HttpWebRequestCount:N0} HttpWebRequest objects on managed heap",
                Evidence: $"{r.HttpWebRequestCount:N0} System.Net.HttpWebRequest objects found. " +
                          "HttpWebRequest is obsolete in .NET 6+ and known to accumulate. " +
                          "Each pending request holds resources until the response is received and disposed. " +
                          "Accumulation indicates synchronous I/O, incomplete cleanup, or timeout hangs.",
                Recommendation:
                    "Migrate to HttpClient (HttpClientFactory in ASP.NET Core). " +
                    "If using HttpWebRequest for legacy reasons, ensure requests complete with timeouts " +
                    "and responses are always disposed. Investigate why requests are pending on the heap.",
                Tags: ["infrastructure", "http", "httpwebrequest", "obsolete"],
                MetricValue: r.HttpWebRequestCount,
                MetricUnit: "HttpWebRequest objects"));
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

        // ── HttpMessageHandler accumulation ───────────────────────────────────
        if (r.HttpMessageHandlerCount >= 10)
        {
            FindingSeverity handlerSev = r.HttpMessageHandlerCount >= 50 ? FindingSeverity.Critical : FindingSeverity.Warning;

            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: handlerSev,
                Title: $"{r.HttpMessageHandlerCount:N0} HttpMessageHandler instances on managed heap",
                Evidence: $"{r.HttpMessageHandlerCount:N0} HttpMessageHandler/SocketsHttpHandler/DelegatingHandler instances found. " +
                          "Handlers own the underlying socket pool, so accumulation is more resource-costly per instance than a bare HttpClient. " +
                          "Common causes: IHttpClientFactory handler rotation, handlers leaked by code that captures them directly " +
                          "instead of always resolving HttpClient from the factory, or long DelegatingHandler middleware chains " +
                          "(logging/auth/retry) each contributing their own wrapper instances." +
                          BuildTopHandlerModuleEvidence(r),
                Recommendation:
                    "If using IHttpClientFactory, check the expired handler tracking entry count and handler-per-client ratio " +
                    "in this report for rotation/leak signals. Otherwise, ensure handlers are long-lived singletons shared " +
                    "across HttpClient instances rather than created per-request or per-client.",
                Tags: ["infrastructure", "http", "httpmessagehandler", "sockets"],
                MetricValue: r.HttpMessageHandlerCount,
                MetricUnit: "HttpMessageHandler instances"));
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
                          "ServicePointManager.MaxServicePoints defaults to 0 (unlimited), which can cause OOM." +
                          BuildLowConnectionLimitEvidence(r),
                Recommendation:
                    "Set ServicePointManager.MaxServicePoints to a reasonable bound (e.g. 100). " +
                    "ServicePoint is obsolete in .NET 6+; prefer HttpClient/SocketsHttpHandler. " +
                    "Investigate whether the application is calling many distinct hostnames.",
                Tags: ["infrastructure", "http", "servicepoint", "sockets"],
                MetricValue: r.ServicePointCount,
                MetricUnit: "ServicePoint objects"));
        }

        // ── IHttpClientFactory handler churn ──────────────────────────────────
        if (r.ExpiredHandlerTrackingEntryCount >= 20)
        {
            findings.Add(new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Infrastructure",
                Severity: FindingSeverity.Warning,
                Title: $"{r.ExpiredHandlerTrackingEntryCount:N0} expired IHttpClientFactory handler tracking entries",
                Evidence: $"{r.ExpiredHandlerTrackingEntryCount:N0} Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry objects found " +
                          $"({r.ActiveHandlerTrackingEntryCount:N0} currently active). Each expired entry represents one " +
                          "SocketsHttpHandler rotation; it is only freed once nothing still references the old handler. " +
                          "A persistently high count indicates a short HandlerLifetime or code holding a handler directly " +
                          "instead of obtaining HttpClient through the factory each time.",
                Recommendation:
                    "Increase IHttpClientFactory's HandlerLifetime if rotations are too frequent for the workload. " +
                    "Audit code paths for anything that captures HttpMessageHandler/SocketsHttpHandler directly " +
                    "rather than always resolving HttpClient from the factory.",
                Tags: ["infrastructure", "http", "httpclientfactory", "handler"],
                MetricValue: r.ExpiredHandlerTrackingEntryCount,
                MetricUnit: "expired handler tracking entries"));
        }

        return findings;
    }

    // Gen2 confirms long-lived reuse; Gen0/Gen1 confirms per-request allocation churn — the same
    // instance count can mean opposite things depending on generation, so surface it whenever a
    // generation was actually resolved (entry.Generation is -1/unresolved when it wasn't).
    private static string BuildGenerationEvidence(HttpObjectDomainResult r)
    {
        long classified = r.HttpClientGen0Count + r.HttpClientGen1Count + r.HttpClientGen2Count;
        if (classified == 0) return string.Empty;

        double gen0Fraction = r.HttpClientGen0Count * 100.0 / classified;
        double gen2Fraction = r.HttpClientGen2Count * 100.0 / classified;

        if (gen0Fraction > 50.0)
            return $" {gen0Fraction:F0}% are Gen0, consistent with per-request allocation rather than reuse.";
        if (gen2Fraction > 50.0)
            return $" {gen2Fraction:F0}% are Gen2, consistent with long-lived reuse — the count itself may be by design (e.g. one per named client).";
        return string.Empty;
    }

    // ServicePoint *count* alone doesn't say whether any of them constrain throughput — a low
    // ConnectionLimit (the historical .NET default was 2) causes queuing latency under load even
    // with just one such ServicePoint. Only sampled instances carry this field, so absence of a
    // resolved value (unsampled dumps, or a fallback path with no per-instance sampler) means
    // this clause is silently omitted rather than guessed at.
    private static string BuildLowConnectionLimitEvidence(HttpObjectDomainResult r)
    {
        int? lowest = null;
        foreach (HttpInstanceSnapshot s in r.TopHttpInstances)
        {
            if (s.Category != "ServicePoint" || s.ConnectionLimit is not int limit)
                continue;
            if (lowest is null || limit < lowest)
                lowest = limit;
        }

        if (lowest is not int lowestLimit || lowestLimit > 4)
            return string.Empty;

        return $" At least one sampled ServicePoint has ConnectionLimit={lowestLimit}, which can cause request queuing under concurrent load to that endpoint.";
    }

    // Points at the P2-5 module-breakdown table instead of leaving the handler count as an
    // opaque number — without this, HandlerModules exists in the domain result but nothing
    // in the report tells the reader it's there or what it says.
    private static string BuildTopHandlerModuleEvidence(HttpObjectDomainResult r)
    {
        if (r.HandlerModules.Count == 0) return string.Empty;

        HttpHandlerModuleSummary top = r.HandlerModules[0];
        return $" Largest contributor: {top.ModuleName} ({top.Count:N0} instances) — see the HttpMessageHandler by module table for the full breakdown.";
    }
}
