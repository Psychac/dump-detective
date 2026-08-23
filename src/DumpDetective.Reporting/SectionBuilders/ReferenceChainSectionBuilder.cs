using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class ReferenceChainSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
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
            for (int i = 0; i < traces.Count; i++)
            {
                var trace = traces[i];
                string status = trace.HasGcRoot
                    ? "GC root found"
                    : trace.TraversalLimited
                        ? "No root (search limit)"
                        : "No root";
                if (!trace.SampleAddress.HasValue)
                    status = "Sample unavailable";

                IReadOnlyList<string>? rootHops = trace.HasGcRoot ? trace.PathHops : null;

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
            for (int i = 0; i < chains.Count; i++)
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
        var retainedTypes = d.RetainedTypeNames ?? [];
        if (retainedTypes.Count > 0)
        {
            var rtRows = new List<TableRow>(retainedTypes.Count);
            for (int i = 0; i < retainedTypes.Count; i++)
                rtRows.Add(new TableRow([Cell(FormatHelper.TruncateString(retainedTypes[i], 80))]));
            compactTables.Add(STCompact("Retained types", new[] { CH("Type") }, rtRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
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
