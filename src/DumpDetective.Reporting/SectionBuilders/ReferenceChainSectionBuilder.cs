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
        var blocks = new List<SectionBlock>();

        blocks.Add(H("REFERENCE CHAIN ANALYSIS"));
        blocks.Add(Divider());
        blocks.Add(M("Analyzed Samples",   $"{d.AnalyzedSamples:N0}", d.AnalyzedSamples));
        blocks.Add(M("Retained Samples",   $"{d.RetainedSamples:N0}", d.RetainedSamples));
        blocks.Add(M("Retained Percentage",$"{d.RetainedPercent:F1}%", d.RetainedPercent));

        var traces = d.TopTypeSampleTraces ?? [];
        if (traces.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP TYPE SAMPLE TRACES"));
            blocks.Add(Divider());

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
                blocks.Add(M("Count",  $"{trace.Count:N0}",                        trace.Count,   indent: 1));
                blocks.Add(M("Total Size", FormatHelper.FormatBytes(trace.TotalSizeBytes), (double)trace.TotalSizeBytes, indent: 1));
                if (trace.SampleAddress.HasValue)
                {
                    blocks.Add(M("Sample Instance", $"0x{trace.SampleAddress.Value:X}", indent: 1));
                    blocks.Add(M("Size",   FormatHelper.FormatBytes(trace.SampleObjectSize), (double)trace.SampleObjectSize, indent: 1));
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
            blocks.Add(Blank());
            blocks.Add(H("TOP RETAINED SAMPLED TYPES"));
            blocks.Add(Divider());

            var rtRows = new List<TableRow>(Math.Min(topRetained.Count, 8));
            int rtLimit = Math.Min(topRetained.Count, 8);
            for (int i = 0; i < rtLimit; i++)
                rtRows.Add(new TableRow([Cell(FormatHelper.TruncateString(topRetained[i].Name, 80)), Cell($"{topRetained[i].Count:N0} retained sample(s)", topRetained[i].Count)]));
            blocks.Add(new TableBlock("Top retained types", ["Type", "Retained Samples"], rtRows));
        }

        var chains = d.SampleReferenceChains ?? [];
        if (chains.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H($"REFERENCE CHAINS (top {Math.Min(chains.Count, MaxChains)})"));
            blocks.Add(Divider());

            int chainLimit = Math.Min(chains.Count, MaxChains);
            for (int i = 0; i < chainLimit; i++)
            {
                blocks.Add(CollapseBegin($"Chain [{i + 1}]"));
                blocks.Add(new PathBlock("Chain", chains[i], 1));
                blocks.Add(CollapseEnd());
            }
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
