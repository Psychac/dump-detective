using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total HTTP Objects",   $"{d.TotalHttpObjects:N0}",      d.TotalHttpObjects),
            KM("HttpClient",           $"{d.HttpClientCount:N0}",       d.HttpClientCount),
            KM("HttpWebRequest",       $"{d.HttpWebRequestCount:N0}",   d.HttpWebRequestCount),
            KM("HttpWebResponse",      $"{d.HttpWebResponseCount:N0}",  d.HttpWebResponseCount),
            KM("Handlers",             $"{d.HttpMessageHandlerCount:N0}", d.HttpMessageHandlerCount),
            KM("ServicePoint",         $"{d.ServicePointCount:N0}",     d.ServicePointCount),
            KM("Total Heap Size",      FormatBytes(d.TotalBytes)),
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
            tables.Add(ST("HTTP objects by type",
                ["Type", "Count", "Heap Size"],
                typeRows));
        }

        if (d.HttpClientCount >= 5)
            blocks.Add(new TextBlock(
                "HttpClient is designed for long-lived reuse. Use IHttpClientFactory or a static HttpClient. " +
                "Creating per-request instances exhausts ephemeral TCP ports even before GC can collect them."));

        return new AnalyzerDetailSection(AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables);
    }
}
