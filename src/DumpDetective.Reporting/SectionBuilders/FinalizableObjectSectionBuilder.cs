using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class FinalizableObjectSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    private const int TopTypeRows = 20;
    private const int TopQueueRows = 10;

    public string AnalyzerName => "Finalizable Object Analysis";
    public int SortOrder => 46; // §21 finalizable objects

    public bool CanHandle(AnalyzerDomainResult result) => result is FinalizableObjectDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (FinalizableObjectDomainResult)result;
        var blocks = new List<SectionBlock>();

        // ── Summary ──────────────────────────────────────────────────────────
        blocks.Add(H("FINALIZABLE OBJECT SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Total Finalizable Objects", $"{d.TotalFinalizableObjects:N0}", d.TotalFinalizableObjects));
        blocks.Add(M("Total Finalizable Memory", FormatHelper.FormatBytes(d.TotalFinalizableBytes)));
        blocks.Add(M("Gen 0 / Gen 1 / Gen 2", $"{d.Gen0Count:N0} / {d.Gen1Count:N0} / {d.Gen2Count:N0}"));
        blocks.Add(M("Finalizer Queue Objects", $"{d.FinalizerQueueCount:N0}", d.FinalizerQueueCount));
        blocks.Add(M("Finalizer Queue Retained Memory", FormatHelper.FormatBytes(d.FinalizerQueueRetainedBytes)));
        if (d.PotentialResurrectionDetected)
            blocks.Add(M("Potential Object Resurrection", "Yes — undisposed IDisposable types in queue", 1.0));

        // ── Top finalizable types by Gen2 count ──────────────────────────────
        if (d.TopFinalizableTypesByGen2Count.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP FINALIZABLE TYPES BY GEN2 COUNT"));
            blocks.Add(T("Long-lived finalizable objects in Gen2 are a common source of memory pressure and finalizer bottlenecks."));
            int limit = Math.Min(d.TopFinalizableTypesByGen2Count.Count, TopTypeRows);
            blocks.Add(new TableBlock(
                Caption: "Top finalizable types by Gen2 object count",
                Headers: ["Type Name", "Gen 0", "Gen 1", "Gen 2", "LOH"],
                Rows: BuildTypeRows(d.TopFinalizableTypesByGen2Count, limit)));
            if (d.TopFinalizableTypesByGen2Count.Count > limit)
                blocks.Add(T($"Showing top {limit} finalizable types by Gen2 count. {d.TopFinalizableTypesByGen2Count.Count - limit} additional type(s) omitted."));
        }

        // ── Top finalizer queue entries by retained size ──────────────────────
        if (d.TopQueueEntriesByRetainedSize.Count > 0)
        {
            blocks.Add(Blank());
            blocks.Add(H("TOP FINALIZER QUEUE ENTRIES BY RETAINED SIZE"));
            blocks.Add(T("Objects awaiting finalization. IDisposable types that are not yet disposed may indicate resource leaks or object resurrection."));
            int limit = Math.Min(d.TopQueueEntriesByRetainedSize.Count, TopQueueRows);
            blocks.Add(new TableBlock(
                Caption: "Top finalizer queue entries by estimated retained size",
                Headers: ["Type Name", "Shallow Size", "Est. Retained", "IDisposable", "Disposed"],
                Rows: BuildQueueRows(d.TopQueueEntriesByRetainedSize, limit)));
            if (d.TopQueueEntriesByRetainedSize.Count > limit)
                blocks.Add(T($"Showing top {limit} finalizer queue entries. {d.TopQueueEntriesByRetainedSize.Count - limit} additional entries omitted."));
        }

        return new AnalyzerDetailSection(AnalyzerName, "Finalizable Object Analysis", SortOrder, blocks);
    }

    private static List<TableRow> BuildTypeRows(IReadOnlyList<TypeGenerationProfile> types, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            TypeGenerationProfile t = types[i];
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(t.TypeName, 70)),
                Cell($"{t.Gen0Count:N0}", t.Gen0Count),
                Cell($"{t.Gen1Count:N0}", t.Gen1Count),
                Cell($"{t.Gen2Count:N0}", t.Gen2Count),
                Cell($"{t.LohCount:N0}",  t.LohCount),
            ]));
        }
        return rows;
    }

    private static List<TableRow> BuildQueueRows(IReadOnlyList<FinalizerQueueEntry> entries, int limit)
    {
        var rows = new List<TableRow>(limit);
        for (int i = 0; i < limit; i++)
        {
            FinalizerQueueEntry e = entries[i];
            string disposedLabel = e.IsDisposableType
                ? (e.DisposedFieldFound ? (e.DisposedFieldValue ? "Yes" : "No (not disposed)") : "Unknown")
                : "N/A";
            rows.Add(new TableRow([
                Cell(FormatHelper.TruncateString(e.TypeName, 70)),
                Cell(FormatHelper.FormatBytes(e.ShallowSize)),
                Cell(FormatHelper.FormatBytes(e.EstimatedRetainedBytes)),
                Cell(e.IsDisposableType ? "Yes" : "No"),
                Cell(disposedLabel),
            ]));
        }
        return rows;
    }
}
