using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Trend.Comparers
{
    internal sealed class HttpObjectTrendComparer : IAnalyzerTrendComparer
    {
        public string AnalyzerName => "HTTP Object Analysis";

        public IReadOnlyList<AnalyzerMetric> ExtractMetrics(AnalyzerDomainResult result)
        {
            if (result is not HttpObjectDomainResult r) return [];
            var metrics = new List<AnalyzerMetric>
            {
                new("http.total",           null, r.TotalHttpObjects,          "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.httpclient",      null, r.HttpClientCount,           "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.webrequest",      null, r.HttpWebRequestCount,       "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.webresponse",     null, r.HttpWebResponseCount,      "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.messagehandler",  null, r.HttpMessageHandlerCount,   "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.servicepoint",    null, r.ServicePointCount,         "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.activehandlertrackingentry",  null, r.ActiveHandlerTrackingEntryCount,  "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.expiredhandlertrackingentry", null, r.ExpiredHandlerTrackingEntryCount, "objects",  MetricTrendDirection.HigherIsWorse),
                new("http.httpclient.gen0",  null, r.HttpClientGen0Count, "objects", MetricTrendDirection.HigherIsWorse),
                new("http.httpclient.gen1",  null, r.HttpClientGen1Count, "objects", MetricTrendDirection.HigherIsWorse),
                new("http.httpclient.gen2",  null, r.HttpClientGen2Count, "objects", MetricTrendDirection.Neutral),
                new("http.handlerratio",    null, HandlerClientRatio(r),      "ratio",    MetricTrendDirection.HigherIsWorse),
                new("http.bytes",           null, r.TotalBytes,               "bytes",    MetricTrendDirection.HigherIsWorse),
            };

            foreach (HttpObjectTypeSummary t in r.ByType)
            {
                metrics.Add(new("http.type.count", t.TypeName, t.Count, "objects", MetricTrendDirection.HigherIsWorse));
                metrics.Add(new("http.type.bytes", t.TypeName, t.TotalBytes, "bytes", MetricTrendDirection.HigherIsWorse));
            }

            return metrics;
        }

        public IReadOnlyList<MetricDelta> Compare(AnalyzerDomainResult baseline, AnalyzerDomainResult current)
        {
            if (baseline is not HttpObjectDomainResult b || current is not HttpObjectDomainResult c) return [];
            return
            [
                MetricDeltaHelper.Compute("http.total",           null, b.TotalHttpObjects,          c.TotalHttpObjects,          "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.httpclient",      null, b.HttpClientCount,           c.HttpClientCount,           "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.webrequest",      null, b.HttpWebRequestCount,       c.HttpWebRequestCount,       "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.webresponse",     null, b.HttpWebResponseCount,      c.HttpWebResponseCount,      "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.messagehandler",  null, b.HttpMessageHandlerCount,   c.HttpMessageHandlerCount,   "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.servicepoint",    null, b.ServicePointCount,         c.ServicePointCount,         "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.activehandlertrackingentry",  null, b.ActiveHandlerTrackingEntryCount,  c.ActiveHandlerTrackingEntryCount,  "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.expiredhandlertrackingentry", null, b.ExpiredHandlerTrackingEntryCount, c.ExpiredHandlerTrackingEntryCount, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.httpclient.gen0", null, b.HttpClientGen0Count, c.HttpClientGen0Count, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.httpclient.gen1", null, b.HttpClientGen1Count, c.HttpClientGen1Count, "objects", MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.httpclient.gen2", null, b.HttpClientGen2Count, c.HttpClientGen2Count, "objects", MetricTrendDirection.Neutral),
                MetricDeltaHelper.Compute("http.handlerratio",    null, HandlerClientRatio(b),      HandlerClientRatio(c),      "ratio",   MetricTrendDirection.HigherIsWorse),
                MetricDeltaHelper.Compute("http.bytes",           null, b.TotalBytes,               c.TotalBytes,               "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }

        // Handlers-per-client ratio: a rising trend across dumps in the same session may
        // indicate handler leaks (rotated-out handlers not being freed). 0 when there are no
        // HttpClient instances to divide by, rather than propagating NaN/Infinity into trends.
        private static double HandlerClientRatio(HttpObjectDomainResult r) =>
            r.HttpClientCount > 0 ? r.HttpMessageHandlerCount / (double)r.HttpClientCount : 0.0;
    }
}


