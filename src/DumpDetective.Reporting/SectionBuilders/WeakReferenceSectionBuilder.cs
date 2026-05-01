using DumpDetective.Analysis.Models;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class WeakReferenceSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypesToShow = 15;

    public string AnalyzerName => "Weak Reference Analysis";
    public int SortOrder => 49; // §24 — after AsyncStateMachine (48)

    public bool CanHandle(AnalyzerDomainResult result) => result is WeakReferenceDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (WeakReferenceDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── §24.1 Weak GC Handle Population ──────────────────────────────────
        blocks.Add(H("WEAK GC HANDLE POPULATION"));
        blocks.Add(Divider());
        blocks.Add(M("Total weak handles",   $"{d.TotalWeakHandles:N0}",    d.TotalWeakHandles));
        blocks.Add(M("Alive targets",        $"{d.AliveWeakTargets:N0}",    d.AliveWeakTargets));
        blocks.Add(M("Dead targets",         $"{d.DeadWeakTargets:N0}",     d.DeadWeakTargets));
        blocks.Add(M("Dead target ratio",    $"{d.DeadTargetRatio:P1}",     d.DeadTargetRatio));

        if (d.ScanCapped)
            blocks.Add(T("⚠ Handle scan was capped at 50 000 entries — totals may be underestimated."));

        // Top alive-target type breakdown
        if (d.TopWeakTargetTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP ALIVE WEAK TARGET TYPES"));
            blocks.Add(Divider());

            var rows = new List<TableRow>(d.TopWeakTargetTypes.Count);
            foreach (NameCountEntry e in d.TopWeakTargetTypes.Take(TopTypesToShow))
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));

            blocks.Add(new TableBlock("Top alive weak target types", ["Type", "Count"], rows));
        }

        // ── §24.2 WeakReference<T> Object Analysis ────────────────────────────
        blocks.Add(Blank());
        blocks.Add(H("WEAKREFERENCE<T> OBJECT ANALYSIS"));
        blocks.Add(Divider());
        blocks.Add(M("WeakReference objects",       $"{d.WeakReferenceObjectCount:N0}",                       d.WeakReferenceObjectCount));
        blocks.Add(M("WeakReference object bytes",  FormatHelper.FormatBytes(d.WeakReferenceObjectBytes),     (double)d.WeakReferenceObjectBytes));
        blocks.Add(M("Stale wrappers (m_handle=0)", $"{d.StaleWrapperCount:N0}",                              d.StaleWrapperCount));

        if (d.TopStaleWrapperHolderTypes.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP STALE WRAPPER HOLDER TYPES"));
            blocks.Add(Divider());

            var rows = new List<TableRow>(d.TopStaleWrapperHolderTypes.Count);
            foreach (NameCountEntry e in d.TopStaleWrapperHolderTypes.Take(TopTypesToShow))
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));

            blocks.Add(new TableBlock("Top stale wrapper holder types", ["Type", "Count"], rows));
        }

        // ── §24.3 ConditionalWeakTable Dead-Key Analysis ──────────────────────
        blocks.Add(Blank());
        blocks.Add(H("CONDITIONAL WEAK TABLE — DEAD KEY ANALYSIS"));
        blocks.Add(Divider());
        blocks.Add(M("Dependent handles with dead primary key",
            $"{d.DependentHandleDeadKeyCount:N0}", d.DependentHandleDeadKeyCount));

        return new AnalyzerDetailSection(AnalyzerName, "Weak Reference Analysis", SortOrder, blocks);
    }
}
