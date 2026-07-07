using DumpDetective.Core.Enums;
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
        var typeTraces = new List<TypeSampleTrace>();
        if (traces.Count > 0)
        {
            int limit = Math.Min(traces.Count, MaxTraces);
            for (int i = 0; i < limit; i++)
            {
                var trace = traces[i];
                string status = trace.HasGcRoot
                    ? "GC root found"
                    : trace.TraversalLimited
                        ? "No root (search limit)"
                        : "No root";
                if (!trace.SampleAddress.HasValue)
                    status = "Sample unavailable";

                IReadOnlyList<string>? rootHops = null;
                if (trace.HasGcRoot && !string.IsNullOrWhiteSpace(trace.RootPath))
                {
                    // Raw format: "root → TypeA → TypeB → target" — split on arrow separators
                    var hops = trace.RootPath!
                        .Split([" → ", " -> "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    rootHops = hops.Length > 0 ? hops : null;
                }

                typeTraces.Add(new TypeSampleTrace(
                    TypeName:         trace.TypeName,
                    Count:            trace.Count,
                    TotalSizeBytes:   trace.TotalSizeBytes,
                    SampleAddress:    trace.SampleAddress.HasValue ? $"0x{trace.SampleAddress.Value:X}" : null,
                    SampleObjectSize: trace.SampleObjectSize,
                    HasGcRoot:        trace.HasGcRoot,
                    RootHops:         rootHops,
                    TraversalLimited: trace.TraversalLimited,
                    StatusLabel:      status));
            }
        }

        var chains = d.SampleReferenceChains ?? [];
        if (chains.Count > 0)
        {
            int chainLimit = Math.Min(chains.Count, MaxChains);
            for (int i = 0; i < chainLimit; i++)
            {
                var hops = chains[i]
                    .Split([" → ", " -> "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                typeTraces.Add(new TypeSampleTrace(
                    TypeName:         $"Chain [{i + 1}]",
                    Count:            1,
                    TotalSizeBytes:   0,
                    SampleAddress:    null,
                    SampleObjectSize: 0,
                    HasGcRoot:        true,
                    RootHops:         hops.Length > 0 ? hops : [chains[i]],
                    TraversalLimited: false,
                    StatusLabel:      "Reference chain"));
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

        return new AnalyzerDetailSection(
            AnalyzerName: AnalyzerName,
            DisplayTitle: AnalyzerName,
            SortOrder: SortOrder,
            Blocks: blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null,
            TypeTraces: typeTraces.Count > 0 ? typeTraces : null);
    }
}
