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
                MetricDeltaHelper.Compute("http.bytes",           null, b.TotalBytes,               c.TotalBytes,               "bytes",   MetricTrendDirection.HigherIsWorse),
            ];
        }
    }
}


