using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules.Insight;

/// <summary>
/// Replaces <c>InsightEngine.DetectHttpClientAccumulation</c> — see
/// docs/refactor/modularity/phase-5-step6-insight-engine-plan.md § 6.2.
/// </summary>
internal sealed class HttpClientAccumulationInsightRule : ISynthesisRule
{
    public string RuleId => "insight.http-client-accumulation";
    public int Version => 1;

    public ObservationQuery Query { get; } = new() { ObservationTypes = ["infra.http-object-population"] };

    public ValueTask<IReadOnlyList<Claim>> SynthesizeAsync(
        ObservationMatchSet matched, SynthesisContext context, CancellationToken cancellationToken = default)
    {
        if (matched.Matched.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        Observation http = matched.Matched[0];
        long totalHttpObjects = (long)http.Measures["total.count"].Value;
        if (totalHttpObjects == 0)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        long httpClientCount = (long)http.Measures["http-client.count"].Value;
        long httpWebResponseCount = (long)http.Measures["http-web-response.count"].Value;

        // Only flag when HttpClient and HttpWebResponse are both present, suggesting mixed API usage.
        if (httpClientCount < 3 || httpWebResponseCount < 10)
            return ValueTask.FromResult<IReadOnlyList<Claim>>([]);

        return ValueTask.FromResult<IReadOnlyList<Claim>>(
        [
            new Claim
            {
                Fingerprint = "insightengine::http-client-accumulation",
                Analyzer = "InsightEngine",
                Category = "Infrastructure",
                Severity = Severity.Warning,
                Title = "Mixed HTTP API usage: HttpClient and HttpWebResponse both present",
                Narrative = $"{httpClientCount:N0} HttpClient instance(s) and " +
                            $"{httpWebResponseCount:N0} HttpWebResponse object(s) found simultaneously. " +
                            "Mixed usage suggests an incomplete migration from HttpWebRequest to HttpClient.",
                Recommendation = "Consolidate all HTTP calls to HttpClient/IHttpClientFactory. " +
                                 "HttpWebRequest and HttpWebResponse are legacy and do not benefit from " +
                                 "modern connection pooling or HTTP/2 support.",
                Tags = ["infrastructure", "http", "httpclient", "legacy"],
                MetricValue = totalHttpObjects,
                MetricUnit = "HTTP objects",
                DerivedFrom = [http.Id],
                Confidence = ConfidenceBreakdown.MeasurementCertain,
            },
        ]);
    }
}
