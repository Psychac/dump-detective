using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Trend.Comparers;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class HttpObjectTrendComparerTests
{
    private static HttpObjectDomainResult MakeResult(long httpClients, long handlers) =>
        new(
            HttpObjectsFound: httpClients > 0 || handlers > 0,
            TotalHttpObjects: httpClients + handlers,
            HttpClientCount: httpClients,
            HttpWebRequestCount: 0,
            HttpWebResponseCount: 0,
            HttpMessageHandlerCount: handlers,
            ServicePointCount: 0,
            ActiveHandlerTrackingEntryCount: 0,
            ExpiredHandlerTrackingEntryCount: 0,
            HttpClientGen0Count: 0,
            HttpClientGen1Count: 0,
            HttpClientGen2Count: 0,
            TotalBytes: 0,
            ByType: [],
            TopHttpInstances: [],
            HandlerModules: []);

    [Fact]
    public void ExtractMetrics_EmitsHandlerRatio()
    {
        var result = MakeResult(httpClients: 5, handlers: 15);

        var metrics = new HttpObjectTrendComparer().ExtractMetrics(result);

        metrics.Should().Contain(m => m.Key == "http.handlerratio" && m.Value == 3.0);
    }

    [Fact]
    public void ExtractMetrics_HandlerRatioIsZero_WhenNoHttpClients()
    {
        var result = MakeResult(httpClients: 0, handlers: 15);

        var metrics = new HttpObjectTrendComparer().ExtractMetrics(result);

        metrics.Should().Contain(m => m.Key == "http.handlerratio" && m.Value == 0.0);
    }

    [Fact]
    public void Compare_HandlerRatio_DetectsRisingChurn()
    {
        var baseline = MakeResult(httpClients: 5, handlers: 5);
        var current = MakeResult(httpClients: 5, handlers: 25);

        var deltas = new HttpObjectTrendComparer().Compare(baseline, current);

        deltas.Should().Contain(d => d.Key == "http.handlerratio" && d.Delta == 4.0);
    }
}
