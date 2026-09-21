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
    public void ExtractMetrics_NoTypeBreakdown_EmitsNothing()
    {
        // Headline metrics (including http.handlerratio, HttpObjectAnalyzer.HandlerClientRatio's
        // own formula) retired onto ObservationTrendDiffer — see ObservationDifferAnalyzerOwnership.
        // With an empty ByType, the comparer now has nothing scoped left to emit either.
        var result = MakeResult(httpClients: 5, handlers: 15);

        var metrics = new HttpObjectTrendComparer().ExtractMetrics(result);

        metrics.Should().BeEmpty();
    }

    [Fact]
    public void Compare_AlwaysEmitsNothing()
    {
        // Compare() had no pre-existing scoped delta branches (only ExtractMetrics fed per-type
        // counts/bytes into the timeline), so it always returns empty now that the headline
        // branches are gone — covered by the differ instead.
        var baseline = MakeResult(httpClients: 5, handlers: 5);
        var current = MakeResult(httpClients: 5, handlers: 25);

        var deltas = new HttpObjectTrendComparer().Compare(baseline, current);

        deltas.Should().BeEmpty();
    }
}
