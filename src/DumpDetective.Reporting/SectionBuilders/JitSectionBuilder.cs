using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class JitSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopMethodsToShow = 15;
    private const int TopFrameTypesToShow = 15;

    public string AnalyzerName => "JIT Analysis";
    public string DisplayTitle => "JIT & Code Footprint";
    public int SortOrder => 200; // §G3 — after AppDomainSectionBuilder (120)

    public bool CanHandle(AnalyzerDomainResult result) => result is JitDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (JitDomainResult)result;
        var tables = new List<SectionTable>();
        var blocks = new List<SectionBlock>();

        int totalFrames = d.ManagedFrameCount + d.UnmanagedFrameCount;
        double unmanagedRatio = totalFrames > 0 ? (double)d.UnmanagedFrameCount / totalFrames : 0.0;

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_jit_code_heap"] = new NumericMetricValue((double)d.TotalJitHeapBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalJitHeapBytes)),
            ["jit_manager_count"] = new NumericMetricValue(d.JitManagerCount, MetricUnit.Count),
            ["active_managed_frames"] = new NumericMetricValue(d.ManagedFrameCount, MetricUnit.Count),
            ["runtime_internal_frames"] = new NumericMetricValue(d.UnmanagedFrameCount, MetricUnit.Count),
            ["active_method_instances_on_stacks"] = new NumericMetricValue(d.ActiveMethodsOnStacks, MetricUnit.Count),
            ["tiered_recompilations_observed"] = new NumericMetricValue(d.TieredMethodCount, MetricUnit.Count),
        };
        if (d.JitHeapPctOfTotalProcess > 0.0)
        {
            keyMetrics["jit_heap_pct_of_total_process"] = new NumericMetricValue(d.JitHeapPctOfTotalProcess, MetricUnit.Percent, $"{d.JitHeapPctOfTotalProcess:P1}");
        }
        if (totalFrames > 0)
            keyMetrics["unmanaged_frame_ratio"] = new NumericMetricValue(unmanagedRatio, MetricUnit.Percent, $"{unmanagedRatio:P1}");

        if (d.TopActiveFrameTypes.Count > 0)
        {
            var typeRows = new List<TableRow>(Math.Min(d.TopActiveFrameTypes.Count, TopFrameTypesToShow));
            foreach (NameCountEntry e in d.TopActiveFrameTypes.Take(TopFrameTypesToShow))
                typeRows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            tables.Add(ST("Active frame types (stack hotspots)", ["Type", "Stack Hits"], typeRows));
        }

        if (d.TopLargestMethods.Count > 0)
        {
            blocks.Add(T("ReadyToRun/native image detection is not available through ClrMD here; treat R2R status as N/A."));
            var methodRows = new List<TableRow>(Math.Min(d.TopLargestMethods.Count, TopMethodsToShow));
            foreach (JitMethodSnapshot m in d.TopLargestMethods.Take(TopMethodsToShow))
            {
                ulong total = (ulong)m.HotSize + m.ColdSize;
                string largeFlag = total > 64_000 ? ">64 KB" : string.Empty;
                methodRows.Add(new TableRow([
                    Cell(m.Signature),
                    Cell(FormatHelper.TruncateString(m.DeclaringType, 60)),
                    Cell($"0x{m.NativeCodeAddress:X}"),
                    Cell(FormatHelper.FormatBytes(m.HotSize),  m.HotSize),
                    Cell(FormatHelper.FormatBytes(m.ColdSize), m.ColdSize),
                    Cell(FormatHelper.FormatBytes(total),      (long)total),
                    Cell(m.IsTiered ? "Yes" : "No"),
                    Cell(largeFlag)]));
            }
            tables.Add(ST("Large JIT-compiled methods (native code size)",
                ["Signature", "Declaring Type", "Native Code Addr", "Hot", "Cold", "Total", "Tiered", "Flag"], methodRows));
        }

        if (d.TieredMethodCount == 0)
            blocks.Add(T("No tiered recompilations detected on live thread stacks. " +
                         "Either tiering is disabled or all methods are stable at Tier1."));
        else
            blocks.Add(T($"{d.TieredMethodCount:N0} method(s) observed with multiple native code addresses for the same " +
                         "metadata token (Tier0 → Tier1 recompilation). This is expected behaviour under tiered compilation."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
