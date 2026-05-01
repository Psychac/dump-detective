using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class JitSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopMethodsToShow    = 15;
    private const int TopFrameTypesToShow = 15;

    public string AnalyzerName => "JIT Analysis";
    public int SortOrder => 51; // §19 — after BoxingSectionBuilder (50)

    public bool CanHandle(AnalyzerDomainResult result) => result is JitDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (JitDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── §19.1  JIT Heap Usage ─────────────────────────────────────────────
        blocks.Add(H("JIT CODE HEAP USAGE"));
        blocks.Add(Divider());
        blocks.Add(M("Total JIT code heap",    FormatHelper.FormatBytes(d.TotalJitHeapBytes), (double)d.TotalJitHeapBytes));
        blocks.Add(M("JIT manager count",      $"{d.JitManagerCount:N0}",   d.JitManagerCount));

        if (d.JitHeapPctOfTotalProcess > 0.0)
            blocks.Add(M("JIT heap % of process", $"{d.JitHeapPctOfTotalProcess:P1}", d.JitHeapPctOfTotalProcess));

        // ── §19.2  Compiled Method Analysis ──────────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("COMPILED METHOD ANALYSIS"));
        blocks.Add(Divider());
        blocks.Add(M("Active managed frames",   $"{d.ManagedFrameCount:N0}",    d.ManagedFrameCount));
        blocks.Add(M("Runtime/internal frames", $"{d.UnmanagedFrameCount:N0}",  d.UnmanagedFrameCount));

        int totalFrames = d.ManagedFrameCount + d.UnmanagedFrameCount;
        if (totalFrames > 0)
        {
            double unmanagedRatio = (double)d.UnmanagedFrameCount / totalFrames;
            blocks.Add(M("Unmanaged frame ratio", $"{unmanagedRatio:P1}", unmanagedRatio));
        }

        blocks.Add(M("Active method instances on stacks", $"{d.ActiveMethodsOnStacks:N0}", d.ActiveMethodsOnStacks));

        // Top active frame types
        if (d.TopActiveFrameTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ACTIVE FRAME TYPES (by stack-hit count)", indent: 1));
            blocks.Add(Divider());
            var typeRows = new List<TableRow>(Math.Min(d.TopActiveFrameTypes.Count, TopFrameTypesToShow));
            foreach (NameCountEntry e in d.TopActiveFrameTypes.Take(TopFrameTypesToShow))
            {
                typeRows.Add(new TableRow([
                    Cell(e.Name),
                    Cell($"{e.Count:N0}", e.Count)]));
            }
            blocks.Add(new TableBlock("Active frame types (stack hotspots)",
                ["Type", "Stack Hits"], typeRows));
        }

        // Top largest methods (native code ≥ 64 KB) found on stacks
        if (d.TopLargestMethods.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("LARGE JIT-COMPILED METHODS ON STACKS (≥ 64 KB)", indent: 1));
            blocks.Add(Divider());
            var methodRows = new List<TableRow>(Math.Min(d.TopLargestMethods.Count, TopMethodsToShow));
            foreach (JitMethodSnapshot m in d.TopLargestMethods.Take(TopMethodsToShow))
            {
                ulong total = (ulong)m.HotSize + m.ColdSize;
                methodRows.Add(new TableRow([
                    Cell(m.Signature),
                    Cell(FormatHelper.FormatBytes(m.HotSize),  m.HotSize),
                    Cell(FormatHelper.FormatBytes(m.ColdSize), m.ColdSize),
                    Cell(FormatHelper.FormatBytes(total),      (long)total)]));
            }
            blocks.Add(new TableBlock("Large JIT-compiled methods (native code size)",
                ["Signature", "Hot", "Cold", "Total"], methodRows));
        }

        // ── §19.3  Tiered Compilation & ReadyToRun ────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("TIERED COMPILATION & READYTORUN"));
        blocks.Add(Divider());
        blocks.Add(M("Tiered recompilations observed", $"{d.TieredMethodCount:N0}", d.TieredMethodCount));

        if (d.TieredMethodCount == 0)
            blocks.Add(T("No tiered recompilations detected on live thread stacks. " +
                         "Either tiering is disabled or all methods are stable at Tier1."));
        else
            blocks.Add(T($"{d.TieredMethodCount:N0} method(s) observed with multiple native code addresses for the same " +
                         "metadata token (Tier0 → Tier1 recompilation). This is expected behaviour under tiered compilation."));

        return new AnalyzerDetailSection(AnalyzerName, "JIT & Native Code Footprint", SortOrder, blocks);
    }
}
