using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ReferenceChainSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int MaxTraces = 10;
    private const int MaxChains = 5;

    public string AnalyzerName => "Reference Chain Analysis";
    public int SortOrder => 60;

    public bool CanHandle(AnalyzerDomainResult result) => result is ReferenceChainDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (ReferenceChainDomainResult)result;
        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["analyzed_samples"] = new NumericMetricValue(d.AnalyzedSamples, MetricUnit.Count),
            ["retained_samples"] = new NumericMetricValue(d.RetainedSamples, MetricUnit.Count),
            ["retained_pct"] = new NumericMetricValue(d.RetainedPercent, MetricUnit.Percent, $"{d.RetainedPercent:F1}%"),
        };
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        var traces = d.TopTypeSampleTraces ?? [];
        if (traces.Count > 0)
        {
            int limit = Math.Min(traces.Count, MaxTraces);
            for (int i = 0; i < limit; i++)
            {
                var trace = traces[i];
                string status = trace.HasGcRoot
                    ? "GC root path found"
                    : trace.TraversalLimited
                        ? "No GC root (search limit reached — inconclusive)"
                        : "No GC root (may be eligible for collection)";

                blocks.Add(CollapseBegin($"[{i + 1}] {trace.TypeName} — {trace.Count:N0} objects, {FormatHelper.FormatBytes(trace.TotalSizeBytes)}"));
                blocks.Add(M("Count", $"{trace.Count:N0}", trace.Count, indent: 1));
                blocks.Add(M("Total Size", FormatHelper.FormatBytes(trace.TotalSizeBytes), (double)trace.TotalSizeBytes, indent: 1));
                if (trace.SampleAddress.HasValue)
                {
                    blocks.Add(M("Sample Instance", $"0x{trace.SampleAddress.Value:X}", indent: 1));
                    blocks.Add(M("Size", FormatHelper.FormatBytes(trace.SampleObjectSize), (double)trace.SampleObjectSize, indent: 1));
                    blocks.Add(M("Status", status, indent: 1));
                    if (trace.HasGcRoot && !string.IsNullOrWhiteSpace(trace.RootPath))
                        blocks.Add(new PathBlock("Root Path", trace.RootPath, 1));
                }
                else
                {
                    blocks.Add(M("Status", "Sample instance unavailable for tracing", indent: 1));
                }
                blocks.Add(CollapseEnd());
            }
        }

        var topRetained = d.TopRetainedTypes ?? [];
        if (topRetained.Count > 0)
        {
            var rtRows = new List<TableRow>(Math.Min(topRetained.Count, 8));
            int rtLimit = Math.Min(topRetained.Count, 8);
            for (int i = 0; i < rtLimit; i++)
                rtRows.Add(new TableRow([Cell(FormatHelper.TruncateString(topRetained[i].Name, 80)), Cell($"{topRetained[i].Count:N0} retained sample(s)", topRetained[i].Count)]));
            compactTables.Add(STCompact("Top retained sampled types", new[] { CH("Type"), CH("Retained Samples","number") }, rtRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var chains = d.SampleReferenceChains ?? [];
        if (chains.Count > 0)
        {
            int chainLimit = Math.Min(chains.Count, MaxChains);
            for (int i = 0; i < chainLimit; i++)
            {
                blocks.Add(CollapseBegin($"Chain [{i + 1}]"));
                blocks.Add(new PathBlock("Chain", chains[i], 1));
                blocks.Add(CollapseEnd());
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: AnalyzerName,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
