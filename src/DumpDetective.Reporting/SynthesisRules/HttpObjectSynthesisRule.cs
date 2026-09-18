using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>
/// Replaces <c>HttpObjectFindingGenerator</c> — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// <c>HttpClientCount</c>/<c>HttpWebRequestCount</c>/<c>ServicePointCount</c>/
/// <c>ActiveHandlerTrackingEntryCount</c>/<c>ExpiredHandlerTrackingEntryCount</c> are all
/// reconstructed by counting matched <c>infra.http-object</c> observations grouped by
/// <c>Subjects[0]</c>'s canonical type name — <c>HttpObjectAnalyzer.ClassifyType</c> requires an
/// exact type-name match for every one of these five categories, so the subject's own canonical
/// name already identifies the category; no separate category measure was needed.
/// </remarks>
internal sealed class HttpObjectSynthesisRule : ISynthesisRule
{
    private const long HttpClientWarningThreshold = 5;
    private const long HttpClientCriticalThreshold = 20;
    private const long HttpWebRequestWarningThreshold = 10;
    private const long HttpWebResponseWarningThreshold = 20;
    private const long HttpMessageHandlerWarningThreshold = 10;
    private const long HttpMessageHandlerCriticalThreshold = 50;
    private const long ServicePointWarningThreshold = 50;
    private const long ExpiredHandlerTrackingEntryWarningThreshold = 20;
    private const int LowConnectionLimitThreshold = 4;

    private const string AnalyzerName = "HTTP Object Analysis";

    public string RuleId => "http-object.finding-parity";
    public int Version => 1;

    public ObservationQuery Query { get; } = new()
    {
        ObservationTypes = ["infra.http-object", "infra.http-handler-module", "infra.http-object-population"],
    };

    public ValueTask<IReadOnlyList<Finding>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        Observation? population = null;
        long httpClientCount = 0, httpWebRequestCount = 0, servicePointCount = 0;
        long activeHandlerTrackingEntryCount = 0, expiredHandlerTrackingEntryCount = 0;
        long httpClientGen0 = 0, httpClientGen1 = 0, httpClientGen2 = 0;
        var httpClientIds = new List<ObservationId>();
        var httpWebRequestIds = new List<ObservationId>();
        var servicePointIds = new List<ObservationId>();
        var activeHandlerIds = new List<ObservationId>();
        var expiredHandlerIds = new List<ObservationId>();
        int? lowestConnectionLimit = null;
        Observation? topHandlerModule = null;

        foreach (Observation observation in matched.Matched)
        {
            if (observation.ObservationType == "infra.http-handler-module")
            {
                topHandlerModule ??= observation;
                continue;
            }

            if (observation.ObservationType == "infra.http-object-population")
            {
                population = observation;
                continue;
            }

            if (observation.ObservationType != "infra.http-object") continue;

            string typeName = ((TypeRef)observation.Subjects[0]).CanonicalName;
            switch (typeName)
            {
                case "System.Net.Http.HttpClient":
                    httpClientCount++;
                    httpClientIds.Add(observation.Id);
                    if (observation.Measures.TryGetValue("generation.ordinal", out Measure genMeasure))
                    {
                        int generation = (int)genMeasure.Value;
                        if (generation == 0) httpClientGen0++;
                        else if (generation == 1) httpClientGen1++;
                        else httpClientGen2++;
                    }
                    break;
                case "System.Net.HttpWebRequest":
                    httpWebRequestCount++;
                    httpWebRequestIds.Add(observation.Id);
                    break;
                case "System.Net.ServicePoint":
                    servicePointCount++;
                    servicePointIds.Add(observation.Id);
                    if (observation.Measures.TryGetValue("connection-limit", out Measure limitMeasure))
                    {
                        int limit = (int)limitMeasure.Value;
                        if (lowestConnectionLimit is null || limit < lowestConnectionLimit) lowestConnectionLimit = limit;
                    }
                    break;
                case "Microsoft.Extensions.Http.ActiveHandlerTrackingEntry":
                    activeHandlerTrackingEntryCount++;
                    activeHandlerIds.Add(observation.Id);
                    break;
                case "Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry":
                    expiredHandlerTrackingEntryCount++;
                    expiredHandlerIds.Add(observation.Id);
                    break;
            }
        }

        if (population is null) return ValueTask.FromResult<IReadOnlyList<Finding>>([]);

        long httpWebResponseCount = (long)population.Measures["http-web-response.count"].Value;
        long httpMessageHandlerCount = (long)population.Measures["http-message-handler.count"].Value;

        var findings = new List<Finding>(6);

        // ── HttpClient misuse ────────────────────────────────────────────────────
        if (httpClientCount >= HttpClientWarningThreshold)
        {
            Severity sev = httpClientCount >= HttpClientCriticalThreshold ? Severity.Critical : Severity.Warning;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("httpclient-count"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = sev,
                Title = $"{httpClientCount:N0} HttpClient instances on managed heap",
                Narrative = $"{httpClientCount:N0} System.Net.Http.HttpClient instances found. " +
                            "HttpClient is designed for long-lived reuse; creating per-request instances " +
                            "exhausts ephemeral TCP ports (TIME_WAIT) and can cause connection failures " +
                            "under load even before garbage collection." +
                            BuildGenerationEvidence(httpClientGen0, httpClientGen2, httpClientGen0 + httpClientGen1 + httpClientGen2),
                Recommendation = "Use IHttpClientFactory (ASP.NET Core) or a static/singleton HttpClient. " +
                                  "HttpClientFactory manages handler lifetime and DNS refresh automatically. " +
                                  "If pre-.NET Core: create one HttpClient per base URI and reuse it.",
                Tags = ["infrastructure", "http", "httpclient", "sockets"],
                MetricValue = httpClientCount,
                MetricUnit = "HttpClient instances",
                DerivedFrom = httpClientIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── HttpWebRequest accumulation (obsolete API) ────────────────────────────
        if (httpWebRequestCount >= HttpWebRequestWarningThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("httpwebrequest-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{httpWebRequestCount:N0} HttpWebRequest objects on managed heap",
                Narrative = $"{httpWebRequestCount:N0} System.Net.HttpWebRequest objects found. " +
                            "HttpWebRequest is obsolete in .NET 6+ and known to accumulate. " +
                            "Each pending request holds resources until the response is received and disposed. " +
                            "Accumulation indicates synchronous I/O, incomplete cleanup, or timeout hangs.",
                Recommendation = "Migrate to HttpClient (HttpClientFactory in ASP.NET Core). " +
                                  "If using HttpWebRequest for legacy reasons, ensure requests complete with timeouts " +
                                  "and responses are always disposed. Investigate why requests are pending on the heap.",
                Tags = ["infrastructure", "http", "httpwebrequest", "obsolete"],
                MetricValue = httpWebRequestCount,
                MetricUnit = "HttpWebRequest objects",
                DerivedFrom = httpWebRequestIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── HttpWebResponse not disposed ──────────────────────────────────────────
        if (httpWebResponseCount >= HttpWebResponseWarningThreshold)
        {
            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("httpwebresponse-not-disposed"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{httpWebResponseCount:N0} HttpWebResponse objects on managed heap",
                Narrative = $"{httpWebResponseCount:N0} System.Net.HttpWebResponse objects found. " +
                            "Each live HttpWebResponse holds the underlying network stream open until disposed. " +
                            "Undisposed responses exhaust connection pool slots.",
                Recommendation = "Always dispose HttpWebResponse: 'using var response = (HttpWebResponse)request.GetResponse()'. " +
                                  "Prefer HttpClient over HttpWebRequest for new code.",
                Tags = ["infrastructure", "http", "httpwebresponse", "dispose"],
                MetricValue = httpWebResponseCount,
                MetricUnit = "HttpWebResponse objects",
                DerivedFrom = [population.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── HttpMessageHandler accumulation ───────────────────────────────────────
        if (httpMessageHandlerCount >= HttpMessageHandlerWarningThreshold)
        {
            Severity handlerSev = httpMessageHandlerCount >= HttpMessageHandlerCriticalThreshold ? Severity.Critical : Severity.Warning;
            string moduleEvidence = topHandlerModule is not null
                ? $" Largest contributor: {((ModuleRef)topHandlerModule.Subjects[0]).SimpleName} ({(long)topHandlerModule.Measures["instance.count"].Value:N0} instances) — see the HttpMessageHandler by module table for the full breakdown."
                : string.Empty;

            var derivedFrom = new List<ObservationId> { population.Id };
            if (topHandlerModule is not null) derivedFrom.Add(topHandlerModule.Id);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("httpmessagehandler-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = handlerSev,
                Title = $"{httpMessageHandlerCount:N0} HttpMessageHandler instances on managed heap",
                Narrative = $"{httpMessageHandlerCount:N0} HttpMessageHandler/SocketsHttpHandler/DelegatingHandler instances found. " +
                            "Handlers own the underlying socket pool, so accumulation is more resource-costly per instance than a bare HttpClient. " +
                            "Common causes: IHttpClientFactory handler rotation, handlers leaked by code that captures them directly " +
                            "instead of always resolving HttpClient from the factory, or long DelegatingHandler middleware chains " +
                            "(logging/auth/retry) each contributing their own wrapper instances." +
                            moduleEvidence,
                Recommendation = "If using IHttpClientFactory, check the expired handler tracking entry count and handler-per-client ratio " +
                                  "in this report for rotation/leak signals. Otherwise, ensure handlers are long-lived singletons shared " +
                                  "across HttpClient instances rather than created per-request or per-client.",
                Tags = ["infrastructure", "http", "httpmessagehandler", "sockets"],
                MetricValue = httpMessageHandlerCount,
                MetricUnit = "HttpMessageHandler instances",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── ServicePoint accumulation ──────────────────────────────────────────────
        if (servicePointCount >= ServicePointWarningThreshold)
        {
            string connectionLimitEvidence = lowestConnectionLimit is int limit && limit <= LowConnectionLimitThreshold
                ? $" At least one sampled ServicePoint has ConnectionLimit={limit}, which can cause request queuing under concurrent load to that endpoint."
                : string.Empty;

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("servicepoint-accumulation"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{servicePointCount:N0} ServicePoint objects on managed heap",
                Narrative = $"{servicePointCount:N0} System.Net.ServicePoint objects found. " +
                            "ServicePoint objects accumulate when many distinct remote endpoints are called. " +
                            "ServicePointManager.MaxServicePoints defaults to 0 (unlimited), which can cause OOM." +
                            connectionLimitEvidence,
                Recommendation = "Set ServicePointManager.MaxServicePoints to a reasonable bound (e.g. 100). " +
                                  "ServicePoint is obsolete in .NET 6+; prefer HttpClient/SocketsHttpHandler. " +
                                  "Investigate whether the application is calling many distinct hostnames.",
                Tags = ["infrastructure", "http", "servicepoint", "sockets"],
                MetricValue = servicePointCount,
                MetricUnit = "ServicePoint objects",
                DerivedFrom = servicePointIds,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        // ── IHttpClientFactory handler churn ──────────────────────────────────────
        if (expiredHandlerTrackingEntryCount >= ExpiredHandlerTrackingEntryWarningThreshold)
        {
            var derivedFrom = new List<ObservationId>(expiredHandlerIds);
            derivedFrom.AddRange(activeHandlerIds);

            findings.Add(new Finding
            {
                Fingerprint = Fingerprint("handler-tracking-churn"),
                Analyzer = AnalyzerName,
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = $"{expiredHandlerTrackingEntryCount:N0} expired IHttpClientFactory handler tracking entries",
                Narrative = $"{expiredHandlerTrackingEntryCount:N0} Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry objects found " +
                            $"({activeHandlerTrackingEntryCount:N0} currently active). Each expired entry represents one " +
                            "SocketsHttpHandler rotation; it is only freed once nothing still references the old handler. " +
                            "A persistently high count indicates a short HandlerLifetime or code holding a handler directly " +
                            "instead of obtaining HttpClient through the factory each time.",
                Recommendation = "Increase IHttpClientFactory's HandlerLifetime if rotations are too frequent for the workload. " +
                                  "Audit code paths for anything that captures HttpMessageHandler/SocketsHttpHandler directly " +
                                  "rather than always resolving HttpClient from the factory.",
                Tags = ["infrastructure", "http", "httpclientfactory", "handler"],
                MetricValue = expiredHandlerTrackingEntryCount,
                MetricUnit = "expired handler tracking entries",
                DerivedFrom = derivedFrom,
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<Finding>>(findings);
    }

    private static string BuildGenerationEvidence(long gen0, long gen2, long classified)
    {
        if (classified == 0) return string.Empty;

        double gen0Fraction = gen0 * 100.0 / classified;
        double gen2Fraction = gen2 * 100.0 / classified;

        if (gen0Fraction > 50.0)
            return $" {gen0Fraction:F0}% are Gen0, consistent with per-request allocation rather than reuse.";
        if (gen2Fraction > 50.0)
            return $" {gen2Fraction:F0}% are Gen2, consistent with long-lived reuse — the count itself may be by design (e.g. one per named client).";
        return string.Empty;
    }

    private static string Fingerprint(string suffix) => $"{AnalyzerName}::{suffix}".ToLowerInvariant();
}
