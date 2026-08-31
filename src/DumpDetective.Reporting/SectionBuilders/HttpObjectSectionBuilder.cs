using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class HttpObjectSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "HTTP Object Analysis";
    public string DisplayTitle => "HTTP Objects";
    public int SortOrder => 730;

    public bool CanHandle(AnalyzerDomainResult result) => result is HttpObjectDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (HttpObjectDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_http_objects"] = new NumericMetricValue(d.TotalHttpObjects, MetricUnit.Count),
            ["http_client"] = new NumericMetricValue(d.HttpClientCount, MetricUnit.Count),
            ["http_web_request"] = new NumericMetricValue(d.HttpWebRequestCount, MetricUnit.Count),
            ["http_web_response"] = new NumericMetricValue(d.HttpWebResponseCount, MetricUnit.Count),
            ["handlers"] = new NumericMetricValue(d.HttpMessageHandlerCount, MetricUnit.Count),
            ["service_point"] = new NumericMetricValue(d.ServicePointCount, MetricUnit.Count),
            ["active_handler_tracking_entries"] = new NumericMetricValue(d.ActiveHandlerTrackingEntryCount, MetricUnit.Count),
            ["expired_handler_tracking_entries"] = new NumericMetricValue(d.ExpiredHandlerTrackingEntryCount, MetricUnit.Count),
            ["http_client_gen0"] = new NumericMetricValue(d.HttpClientGen0Count, MetricUnit.Count),
            ["http_client_gen1"] = new NumericMetricValue(d.HttpClientGen1Count, MetricUnit.Count),
            ["http_client_gen2"] = new NumericMetricValue(d.HttpClientGen2Count, MetricUnit.Count),
            ["total_heap_size"] = new NumericMetricValue((double)d.TotalBytes, MetricUnit.Bytes, FormatBytes(d.TotalBytes)),
        };

        if (d.HttpClientCount > 0)
        {
            double handlerClientRatio = d.HttpMessageHandlerCount / (double)d.HttpClientCount;
            keyMetrics["handler_client_ratio"] = new NumericMetricValue(handlerClientRatio, MetricUnit.Count, $"{handlerClientRatio:F1}x");
        }

        if (!d.HttpObjectsFound)
        {
            blocks.Add(new TextBlock("No HTTP-related objects (HttpClient, HttpWebRequest, HttpMessageHandler, ServicePoint) detected on the managed heap."));
            return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
                KeyMetrics: keyMetrics);
        }

        if (d.ByType.Count > 0)
        {
            var typeRows = new List<TableRow>(d.ByType.Count);
            for (int i = 0; i < d.ByType.Count; i++)
            {
                HttpObjectTypeSummary t = d.ByType[i];
                typeRows.Add(new TableRow([
                    Cell(t.TypeName),
                    Cell($"{t.Count:N0}", t.Count),
                    Cell(FormatBytes(t.TotalBytes)),
                ]));
            }
            compactTables.Add(STCompact("HTTP objects by type",
                new[] { CH("Type"), CH("Count","number"), CH("Heap Size","bytes") },
                typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.HttpClientCount >= 5)
            blocks.Add(new TextBlock(
                "HttpClient is designed for long-lived reuse. Use IHttpClientFactory or a static HttpClient. " +
                "Creating per-request instances exhausts ephemeral TCP ports even before GC can collect them."));

        if (d.HttpWebResponseCount >= 20)
            blocks.Add(new TextBlock(
                "Undisposed HttpWebResponse objects hold network streams open, exhausting connection pool slots. " +
                "Always wrap responses in a `using` statement or explicitly call Dispose()."));

        if (d.ServicePointCount >= 50)
            blocks.Add(new TextBlock(
                "ServicePointManager.MaxServicePoints defaults to unlimited, causing ServicePoint accumulation and potential OOM. " +
                "Set a reasonable limit (e.g., 100) or migrate to HttpClient (.NET 6+ preferred)."));

        if (d.ExpiredHandlerTrackingEntryCount >= 20)
            blocks.Add(new TextBlock(
                "A large number of expired IHttpClientFactory handler tracking entries means many SocketsHttpHandler " +
                "rotations have accumulated. Each rotation only frees the previous handler once nothing still " +
                "references it — a persistently high count suggests either a very short HandlerLifetime or code " +
                "holding onto a handler/HttpMessageHandler directly instead of obtaining it through the factory."));

        if (d.TopHttpInstances.Count > 0)
        {
            var instanceRows = new List<TableRow>(d.TopHttpInstances.Count);
            for (int i = 0; i < d.TopHttpInstances.Count; i++)
            {
                HttpInstanceSnapshot s = d.TopHttpInstances[i];
                string detail = s.Category switch
                {
                    "HttpClient" => s.TimeoutMilliseconds >= 0 ? $"Timeout: {s.TimeoutMilliseconds:N0} ms" : "",
                    "HttpWebRequest" => s.ResponsePending ? "Response pending" : "",
                    "ActiveHandlerTrackingEntry" or "ExpiredHandlerTrackingEntry" =>
                        s.ClientName is not null ? $"Client: {s.ClientName}" : "",
                    "ServicePoint" => s.ConnectionLimit is int limit ? $"ConnectionLimit: {limit:N0}" : "",
                    _ => "",
                };
                instanceRows.Add(new TableRow([
                    Cell(s.Category),
                    Cell($"0x{s.Address:X}"),
                    Cell(s.Uri ?? "(n/a)"),
                    Cell(detail),
                ]));
            }
            compactTables.Add(STCompact("HTTP object instances",
                new[] { CH("Category"), CH("Address"), CH("URI"), CH("Detail") },
                instanceRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.HandlerModules.Count > 0)
        {
            var handlerModuleRows = new List<TableRow>(d.HandlerModules.Count);
            for (int i = 0; i < d.HandlerModules.Count; i++)
            {
                HttpHandlerModuleSummary m = d.HandlerModules[i];
                handlerModuleRows.Add(new TableRow([
                    Cell(m.ModuleName),
                    Cell($"{m.Count:N0}", m.Count),
                    Cell(FormatBytes(m.TotalBytes)),
                ]));
            }
            compactTables.Add(STCompact("HttpMessageHandler by module",
                new[] { CH("Module"), CH("Count", "number"), CH("Heap Size") },
                handlerModuleRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
