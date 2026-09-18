using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="HttpObjectFindingGenerator"/> (old) and
/// <see cref="HttpObjectSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class HttpObjectSynthesisRuleParityTests
{
    private readonly HttpObjectFindingGenerator _oldGenerator = new();
    private readonly HttpObjectSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoHttpObjectsFound_ReturnsEmpty() => AssertParity(BuildResult());

    [Fact]
    public Task HttpClientAtWarningThreshold() => AssertParity(BuildResult(httpClientCount: 5, httpClientGen0: 5));

    [Fact]
    public Task HttpClientAtCriticalThreshold() => AssertParity(BuildResult(httpClientCount: 20, httpClientGen0: 20));

    [Fact]
    public Task HttpClientBelowThreshold_NoFinding() => AssertParity(BuildResult(httpClientCount: 3, httpClientGen0: 3));

    [Fact]
    public Task HttpClientMostlyGen0() => AssertParity(BuildResult(httpClientCount: 10, httpClientGen0: 8, httpClientGen1: 2));

    [Fact]
    public Task HttpClientMostlyGen2() => AssertParity(BuildResult(httpClientCount: 10, httpClientGen2: 9, httpClientGen1: 1));

    [Fact]
    public Task HttpClientMixedGenerations_NoQualifierText() => AssertParity(BuildResult(httpClientCount: 10, httpClientGen0: 4, httpClientGen1: 3, httpClientGen2: 3));

    [Fact]
    public Task HttpWebRequestAtWarningThreshold() => AssertParity(BuildResult(httpWebRequestCount: 10));

    [Fact]
    public Task HttpWebRequestBelowThreshold_NoFinding() => AssertParity(BuildResult(httpWebRequestCount: 5));

    [Fact]
    public Task HttpWebResponseAtWarningThreshold() => AssertParity(BuildResult(httpWebResponseCount: 20));

    [Fact]
    public Task HttpWebResponseBelowThreshold_NoFinding() => AssertParity(BuildResult(httpWebResponseCount: 10));

    [Fact]
    public Task HttpMessageHandlerAtWarningThreshold_NoModule() => AssertParity(BuildResult(httpMessageHandlerCount: 10));

    [Fact]
    public Task HttpMessageHandlerAtCriticalThreshold_WithModule() => AssertParity(BuildResult(
        httpMessageHandlerCount: 50, handlerModules: [("Polly.Core", 30), ("MyApp.Handlers", 20)]));

    [Fact]
    public Task ServicePointAtWarningThreshold_NoLowConnectionLimit() => AssertParity(BuildResult(
        servicePointCount: 50, servicePointConnectionLimits: [100, 200]));

    [Fact]
    public Task ServicePointAtWarningThreshold_WithLowConnectionLimit() => AssertParity(BuildResult(
        servicePointCount: 50, servicePointConnectionLimits: [100, 2]));

    [Fact]
    public Task ServicePointBelowThreshold_NoFinding() => AssertParity(BuildResult(servicePointCount: 20));

    [Fact]
    public Task ExpiredHandlerTrackingEntryAtWarningThreshold() => AssertParity(BuildResult(
        expiredHandlerTrackingEntryCount: 20, activeHandlerTrackingEntryCount: 5));

    [Fact]
    public Task ExpiredHandlerTrackingEntryBelowThreshold_NoFinding() => AssertParity(BuildResult(
        expiredHandlerTrackingEntryCount: 10, activeHandlerTrackingEntryCount: 5));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        httpClientCount: 20, httpClientGen2: 20,
        httpWebRequestCount: 15,
        httpWebResponseCount: 25,
        httpMessageHandlerCount: 60, handlerModules: [("Polly.Core", 40)],
        servicePointCount: 60, servicePointConnectionLimits: [2],
        expiredHandlerTrackingEntryCount: 25, activeHandlerTrackingEntryCount: 10));

    private async Task AssertParity(HttpObjectDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(HttpObjectDomainResult result)
    {
        if (result.TotalHttpObjects == 0) return [];

        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };
        var observations = new List<Observation>();

        void AddInstances(string typeName, long count, Func<int, Dictionary<string, Measure>>? measureFactory = null)
        {
            for (int i = 0; i < count; i++)
            {
                var measures = measureFactory?.Invoke(i) ?? new Dictionary<string, Measure>();
                measures["shallow.bytes"] = new Measure(64, MeasureUnit.Bytes, MeasureSemantics.Absolute);

                observations.Add(new Observation
                {
                    Id = ObservationId.NewId(),
                    ObservationType = "infra.http-object",
                    Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
                    When = Point(),
                    Measures = measures,
                    Provenance = Provenance(capabilitiesUsed),
                    Confidence = 1.0,
                });
            }
        }

        var generationSequence = new List<int>();
        generationSequence.AddRange(Enumerable.Repeat(0, (int)result.HttpClientGen0Count));
        generationSequence.AddRange(Enumerable.Repeat(1, (int)result.HttpClientGen1Count));
        generationSequence.AddRange(Enumerable.Repeat(2, (int)result.HttpClientGen2Count));

        AddInstances("System.Net.Http.HttpClient", result.HttpClientCount, i => new Dictionary<string, Measure>
        {
            ["generation.ordinal"] = new Measure(generationSequence[i], MeasureUnit.Custom, MeasureSemantics.Absolute),
        });
        AddInstances("System.Net.HttpWebRequest", result.HttpWebRequestCount);
        AddInstances("Microsoft.Extensions.Http.ActiveHandlerTrackingEntry", result.ActiveHandlerTrackingEntryCount);
        AddInstances("Microsoft.Extensions.Http.ExpiredHandlerTrackingEntry", result.ExpiredHandlerTrackingEntryCount);

        var connectionLimits = new List<int?>();
        foreach (HttpInstanceSnapshot snap in result.TopHttpInstances)
            if (snap.Category == "ServicePoint") connectionLimits.Add(snap.ConnectionLimit);
        while (connectionLimits.Count < result.ServicePointCount) connectionLimits.Add(null);

        AddInstances("System.Net.ServicePoint", result.ServicePointCount, i =>
        {
            var m = new Dictionary<string, Measure>();
            if (connectionLimits[i] is int limit)
                m["connection-limit"] = new Measure(limit, MeasureUnit.Count, MeasureSemantics.Absolute);
            return m;
        });

        foreach (HttpHandlerModuleSummary module in result.HandlerModules)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.http-handler-module",
                Subjects = [new ModuleRef { SimpleName = module.ModuleName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(module.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(new HashSet<Capability> { CapabilityVocabulary.HeapTypes }),
                Confidence = 1.0,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "infra.http-object-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["http-web-response.count"] = new Measure(result.HttpWebResponseCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                ["http-message-handler.count"] = new Measure(result.HttpMessageHandlerCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(capabilitiesUsed),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "HTTP Object Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static HttpObjectDomainResult BuildResult(
        long httpClientCount = 0,
        long httpClientGen0 = 0,
        long httpClientGen1 = 0,
        long httpClientGen2 = 0,
        long httpWebRequestCount = 0,
        long httpWebResponseCount = 0,
        long httpMessageHandlerCount = 0,
        long servicePointCount = 0,
        IReadOnlyList<int>? servicePointConnectionLimits = null,
        long activeHandlerTrackingEntryCount = 0,
        long expiredHandlerTrackingEntryCount = 0,
        IReadOnlyList<(string ModuleName, long Count)>? handlerModules = null)
    {
        long total = httpClientCount + httpWebRequestCount + httpWebResponseCount + httpMessageHandlerCount
            + servicePointCount + activeHandlerTrackingEntryCount + expiredHandlerTrackingEntryCount;

        var topInstances = new List<HttpInstanceSnapshot>();
        if (servicePointConnectionLimits is not null)
        {
            foreach (int limit in servicePointConnectionLimits)
                topInstances.Add(new HttpInstanceSnapshot("ServicePoint", "System.Net.ServicePoint", 0x1000, ConnectionLimit: limit));
        }

        var moduleSummaries = new List<HttpHandlerModuleSummary>();
        if (handlerModules is not null)
        {
            foreach ((string moduleName, long count) in handlerModules)
                moduleSummaries.Add(new HttpHandlerModuleSummary(moduleName, count, 0));
        }

        return new HttpObjectDomainResult(
            HttpObjectsFound: total > 0,
            TotalHttpObjects: total,
            HttpClientCount: httpClientCount,
            HttpWebRequestCount: httpWebRequestCount,
            HttpWebResponseCount: httpWebResponseCount,
            HttpMessageHandlerCount: httpMessageHandlerCount,
            ServicePointCount: servicePointCount,
            ActiveHandlerTrackingEntryCount: activeHandlerTrackingEntryCount,
            ExpiredHandlerTrackingEntryCount: expiredHandlerTrackingEntryCount,
            HttpClientGen0Count: httpClientGen0,
            HttpClientGen1Count: httpClientGen1,
            HttpClientGen2Count: httpClientGen2,
            TotalBytes: 0,
            ByType: [],
            TopHttpInstances: topInstances,
            HandlerModules: moduleSummaries);
    }
}
