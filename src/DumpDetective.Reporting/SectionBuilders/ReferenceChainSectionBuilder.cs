using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

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
        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Analyzed Samples", $"{d.AnalyzedSamples:N0}",    d.AnalyzedSamples),
            KM("Retained Samples", $"{d.RetainedSamples:N0}",    d.RetainedSamples),
            KM("Retained %",       $"{d.RetainedPercent:F1}%",   d.RetainedPercent),
        };
        var tables = new List<SectionTable>();
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
            tables.Add(ST("Top retained sampled types", ["Type", "Retained Samples"], rtRows));
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
            Tables: tables.Count > 0 ? tables : null);
    }
}
