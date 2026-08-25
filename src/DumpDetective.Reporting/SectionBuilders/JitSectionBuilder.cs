using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Linq;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class JitSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "JIT Analysis";
    public string DisplayTitle => "JIT & Code Footprint";
    public int SortOrder => 200; // §G3 — after ModuleSectionBuilder (120)

    public bool CanHandle(AnalyzerDomainResult result) => result is JitDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (JitDomainResult)result;
        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>();

        int totalFrames = d.ManagedFrameCount + d.UnmanagedFrameCount;
        double unmanagedRatio = totalFrames > 0 ? (double)d.UnmanagedFrameCount / totalFrames : 0.0;
        double readyToRunFraction = d.ManagedFrameCount > 0 ? (double)d.ReadyToRunFrameCount / d.ManagedFrameCount : 0.0;
        double methodUniquenessRatio = d.DistinctMethodsOnStacks > 0 ? (double)d.ActiveMethodsOnStacks / d.DistinctMethodsOnStacks : 0.0;

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>
        {
            ["total_jit_code_heap"] = new NumericMetricValue((double)d.TotalJitHeapBytes, MetricUnit.Bytes, FormatHelper.FormatBytes(d.TotalJitHeapBytes)),
            ["jit_manager_count"] = new NumericMetricValue(d.JitManagerCount, MetricUnit.Count),
            ["active_managed_frames"] = new NumericMetricValue(d.ManagedFrameCount, MetricUnit.Count),
            ["runtime_internal_frames"] = new NumericMetricValue(d.UnmanagedFrameCount, MetricUnit.Count),
            ["active_method_instances_on_stacks"] = new NumericMetricValue(d.ActiveMethodsOnStacks, MetricUnit.Count),
            ["distinct_methods_on_stacks"] = new NumericMetricValue(d.DistinctMethodsOnStacks, MetricUnit.Count),
            ["tiered_recompilations_observed"] = new NumericMetricValue(d.TieredMethodCount, MetricUnit.Count),
            ["readytorun_frame_count"] = new NumericMetricValue(d.ReadyToRunFrameCount, MetricUnit.Count),
            ["dynamic_method_frame_count"] = new NumericMetricValue(d.DynamicMethodFrameCount, MetricUnit.Count),
            ["max_thread_frame_depth"] = new NumericMetricValue(d.MaxThreadFrameDepth, MetricUnit.Count,
                d.MaxThreadFrameDepth > 0 ? $"{d.MaxThreadFrameDepth:N0} (OS thread {d.MaxThreadFrameDepthOSThreadId})" : "0"),
        };
        if (totalFrames > 0)
            keyMetrics["unmanaged_frame_ratio"] = new NumericMetricValue(unmanagedRatio, MetricUnit.Percent, $"{unmanagedRatio:P1}");
        if (d.ManagedFrameCount > 0)
            keyMetrics["readytorun_frame_fraction"] = new NumericMetricValue(readyToRunFraction, MetricUnit.Percent, $"{readyToRunFraction:P1}");
        if (d.DistinctMethodsOnStacks > 0)
            keyMetrics["method_uniqueness_ratio"] = new NumericMetricValue(methodUniquenessRatio, MetricUnit.Count, $"{methodUniquenessRatio:F2}x");

        if (d.TopActiveFrameTypes.Count > 0)
        {
            var typeRows = new List<TableRow>(d.TopActiveFrameTypes.Count);
            foreach (NameCountEntry e in d.TopActiveFrameTypes)
                typeRows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Active frame types (stack hotspots)", new[] { CH("Type"), CH("Stack Hits","number") }, typeRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopActiveModulesByFrameHits.Count > 0)
        {
            var moduleRows = new List<TableRow>(d.TopActiveModulesByFrameHits.Count);
            foreach (NameCountEntry e in d.TopActiveModulesByFrameHits)
                moduleRows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            compactTables.Add(STCompact("Active modules (per-module JIT stack heatmap)", new[] { CH("Module"), CH("Stack Hits","number") }, moduleRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.TopLargestMethods.Count > 0)
        {
            var methodRows = new List<TableRow>(d.TopLargestMethods.Count);
            foreach (JitMethodSnapshot m in d.TopLargestMethods)
            {
                ulong total = (ulong)m.HotSize + m.ColdSize;
                string largeFlag = total > d.LargeMethodThresholdBytes ? $">{FormatHelper.FormatBytes(d.LargeMethodThresholdBytes)}" : string.Empty;
                methodRows.Add(new TableRow([
                    Cell(m.Signature),
                    Cell(FormatHelper.TruncateString(m.DeclaringType, 60)),
                    Cell($"0x{m.NativeCodeAddress:X}"),
                    Cell(FormatHelper.FormatBytes(m.HotSize),  m.HotSize),
                    Cell(FormatHelper.FormatBytes(m.ColdSize), m.ColdSize),
                    Cell(FormatHelper.FormatBytes(total),      (long)total),
                    Cell(m.IsTiered ? "Yes" : "No"),
                    Cell(m.IsReadyToRun ? "R2R" : "JIT"),
                    Cell(largeFlag)]));
            }
            compactTables.Add(STCompact("Large JIT-compiled methods (native code size)", new[] { CH("Signature"), CH("Declaring Type"), CH("Native Code Addr"), CH("Hot","bytes"), CH("Cold","bytes"), CH("Total","bytes"), CH("Tiered"), CH("Origin"), CH("Flag") }, methodRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        if (d.DynamicMethodFrameCount > 0)
            blocks.Add(T($"{d.DynamicMethodFrameCount:N0} active frame(s) resolve to a dynamic module " +
                         "(System.Reflection.Emit.DynamicMethod, AssemblyBuilder-emitted types, or a compiled LINQ " +
                         "expression tree). Dynamic codegen is a common source of code-heap growth if generated " +
                         "repeatedly instead of cached."));

        if (d.TieredMethodCount == 0)
            blocks.Add(T("No tiered recompilations detected on live thread stacks. " +
                         "Either tiering is disabled or all methods are stable at Tier1."));
        else
            blocks.Add(T($"~{d.TieredMethodCount:N0} method(s) (estimate, stack-visible methods only) observed with multiple " +
                         "native code addresses for the same method (Tier0 → Tier1 recompilation). This is expected behaviour under tiered compilation."));

        return new AnalyzerDetailSection(
            AnalyzerName, DisplayTitle, SortOrder, blocks,
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }
}
