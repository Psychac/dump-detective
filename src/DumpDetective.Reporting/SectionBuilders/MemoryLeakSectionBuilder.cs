using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class RetentionSectionBuilder : SectionBuilderBase, IAnalyzerSectionBuilder
{
    public string AnalyzerName => "Retention Analysis";
    public int SortOrder => 25;

    public bool CanHandle(AnalyzerDomainResult result) => result is RetentionDomainResult;

    public AnalyzerDetailSection Build(AnalyzerDomainResult result)
    {
        var d = (RetentionDomainResult)result;
        var blocks = new List<SectionBlock>();

        // Finalizer queue: moved to FinalizableObjectSectionBuilder (single source of truth)

        // Retention summary
        blocks.Add(Blank());
        blocks.Add(H("RETENTION SUMMARY"));
        blocks.Add(Divider());
        blocks.Add(M("Highly Referenced Objects", $"{d.HighlyReferencedObjectCount:N0}", d.HighlyReferencedObjectCount));
        blocks.Add(M("Top Highly Referenced Footprint", FormatHelper.FormatBytes(d.TopHighlyReferencedTotalBytes), (double)d.TopHighlyReferencedTotalBytes));

        var topTypes = d.TopRetentionTypes ?? [];
        if (topTypes.Count > 0)
        {
            var typeRows = new List<TableRow>(topTypes.Count);
            for (int i = 0; i < topTypes.Count; i++)
            {
                var type = topTypes[i];
                typeRows.Add(new TableRow([
                    Cell(type.TypeName),
                    Cell($"{type.ObjectCount:N0}", type.ObjectCount),
                    Cell(FormatHelper.FormatBytes(type.TotalBytes), (long)type.TotalBytes),
                    Cell($"{type.TotalIncomingReferences:N0}", type.TotalIncomingReferences),
                    Cell($"{type.MaxIncomingReferences:N0}", type.MaxIncomingReferences)]));
            }

            blocks.Add(new TableBlock("Top retention types (from highly referenced objects)", ["Type", "Objects", "Footprint", "Incoming Refs", "Max Refs"], typeRows));
        }

        // Highly referenced objects
        blocks.Add(Blank());
        blocks.Add(H("TOP HIGHLY REFERENCED OBJECTS"));
        blocks.Add(Divider());

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
