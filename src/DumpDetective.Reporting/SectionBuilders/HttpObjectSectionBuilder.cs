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
            ["total_heap_size"] = new NumericMetricValue((double)d.TotalBytes, MetricUnit.Bytes, FormatBytes(d.TotalBytes)),
        };

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

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
