using DumpDetective.Reporting.SynthesisRules;
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
                Assertion = new Assertion
                {
                    Predicate = AssertionPredicate.Exhibits,
                    Measure = ClaimParts.Count(httpWebResponseCount),
                    Reference = ClaimParts.Budget(ClaimParts.Count(10), "legacy-response threshold"),
                    Qualifiers = new AssertionQualifiers { Location = "HttpWebResponse objects alongside HttpClient", Mechanism = "mixed legacy and modern HTTP APIs" },
                },
                Support = [new EvidenceStatement([http.Id], $"{httpClientCount:N0} HttpClient instances and {httpWebResponseCount:N0} HttpWebResponse objects are on the heap together.")],
                Counter = Counter.None("Some libraries use HttpWebRequest internally, so legacy objects can appear without the application's own code having stayed on the old API."),
                FalsifiedBy =
                [
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.Windbg,
                        Command = "!gcroot <HttpWebResponse address>",
                        Test = "On a sampled HttpWebResponse: if its root is a third-party library, the legacy usage is not the application's own.",
                    },
                    new FalsificationTest
                    {
                        Kind = FalsificationKind.CodeInspection,
                        Test = "If the application code contains no HttpWebRequest calls, the legacy objects come from dependencies.",
                    },
                ],
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
