using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class MemoryLeakSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Memory Leak Analysis";
    public int SortOrder => 25;

    public bool CanHandle(AnalyzerDomainResult result) => result is MemoryLeakDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (MemoryLeakDomainResult)result;
        var blocks = new List<SectionBlock>();

        // Finalizer queue: moved to FinalizableObjectSectionBuilder (single source of truth)

        // Highly referenced objects
        blocks.Add(Blank());
        blocks.Add(H("HIGHLY REFERENCED OBJECTS"));
        blocks.Add(Divider());
        blocks.Add(M("Highly Referenced Objects", $"{d.HighlyReferencedObjectCount:N0}", d.HighlyReferencedObjectCount));

        var topRefs = d.TopHighlyReferencedObjects ?? [];
        if (topRefs.Count > 0)
        {
            var hrRows = new List<TableRow>(topRefs.Count);
            for (int i = 0; i < topRefs.Count; i++)
            {
                var obj = topRefs[i];
                hrRows.Add(new TableRow([
                    Cell(obj.TypeName),
                    Cell($"0x{obj.Address:X}"),
                    Cell(FormatHelper.FormatBytes(obj.Size), (long)obj.Size),
                    Cell($"{obj.IncomingReferences:N0}", obj.IncomingReferences)]));
            }
            blocks.Add(new TableBlock("Top highly referenced objects", ["Type", "Address", "Size", "Incoming Refs"], hrRows));
        }

        if (d.SkippedReferenceAddresses > 0)
        {
            blocks.Add(Blank());
            blocks.Add(T($"Reference tracking cap hit; {d.SkippedReferenceAddresses:N0} addresses skipped — counts above may be partial."));
        }

        return new AnalyzerDetailSection(AnalyzerName, AnalyzerName, SortOrder, blocks);
    }
}
