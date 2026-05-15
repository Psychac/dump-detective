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
        var tables = new List<SectionTable>();

        var keyMetrics = new List<SectionKeyMetric>
        {
            KM("Total weak handles", $"{d.TotalWeakHandles:N0}", d.TotalWeakHandles),
            KM("Alive targets", $"{d.AliveWeakTargets:N0}", d.AliveWeakTargets),
            KM("Dead targets", $"{d.DeadWeakTargets:N0}", d.DeadWeakTargets),
            KM("Dead target ratio", $"{d.DeadTargetRatio:P1}", d.DeadTargetRatio),
            KM("WeakReference objects", $"{d.WeakReferenceObjectCount:N0}", d.WeakReferenceObjectCount),
            KM("WeakReference object bytes", FormatHelper.FormatBytes(d.WeakReferenceObjectBytes), (double)d.WeakReferenceObjectBytes),
            KM("Stale wrappers (m_handle=0)", $"{d.StaleWrapperCount:N0}", d.StaleWrapperCount),
            KM("Dependent handles with dead primary key", $"{d.DependentHandleDeadKeyCount:N0}", d.DependentHandleDeadKeyCount),
        };

        if (d.ScanCapped)
            blocks.Add(T("⚠ Handle scan was capped at 50 000 entries — totals may be underestimated."));

        if (d.WeakHandleKinds.Count > 0)
        {
            var rows = new List<TableRow>(d.WeakHandleKinds.Count);
            foreach (NameCountEntry e in d.WeakHandleKinds.Take(TopTypesToShow))
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            tables.Add(ST("Weak handle kinds", ["Kind", "Count"], rows));
        }

        if (d.TopWeakTargetTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopWeakTargetTypes.Count);
            foreach (NameCountEntry e in d.TopWeakTargetTypes.Take(TopTypesToShow))
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            tables.Add(ST("Top alive weak target types", ["Type", "Count"], rows));
        }

        if (d.TopStaleWrapperHolderTypes.Count > 0)
        {
            var rows = new List<TableRow>(d.TopStaleWrapperHolderTypes.Count);
            foreach (NameCountEntry e in d.TopStaleWrapperHolderTypes.Take(TopTypesToShow))
                rows.Add(new TableRow([Cell(e.Name), Cell($"{e.Count:N0}", e.Count)]));
            tables.Add(ST("Top stale wrapper holder types", ["Type", "Count"], rows));
        }

        // Exported artifacts note (if any)
        if (d.Artifacts is { Count: > 0 })
        {
            blocks.Add(H("EXPORTS"));
            blocks.Add(T("This analyzer produced on-disk exports for deeper offline inspection."));
            foreach (var a in d.Artifacts)
            {
                if (a.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — Pretty JSON; open in VS Code or any JSON viewer."));
                else if (a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase))
                    blocks.Add(Li($"{a.FileName} — NDJSON + gzip (streamable). To inspect: 'gzip -cd {a.FileName} | jq -C '.' or open in 7-Zip/VS Code after extraction."));
                else
                    blocks.Add(Li(a.FileName));
            }
        }

        return new AnalyzerDetailSection(
            AnalyzerName, "Weak Reference Analysis", SortOrder, blocks,
            KeyMetrics: keyMetrics,
            Tables: tables.Count > 0 ? tables : null);
    }
}
