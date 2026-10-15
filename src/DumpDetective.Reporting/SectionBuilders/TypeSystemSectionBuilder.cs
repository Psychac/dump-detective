using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using System.Text.Json;
using System.Linq;
using DumpDetective.Core.Enums;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class TypeSystemSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{

    public string SectionId => "C1";
    public string DisplayTitle => "Type Shape and Finalizable Overhead";
    public int SortOrder => 100;

    public bool CanBuild(AnalyzerResultSet results)
        => results.Get<MemoryDomainResult>() is not null
        || results.Get<GCGenerationDomainResult>() is not null
        || results.Get<ObjectShapeAnalyzerDomainResult>() is not null
        || results.Get<GCRootDomainResult>() is not null;

    public AnalyzerDetailSection Build(AnalyzerResultSet results)
    {
        MemoryDomainResult? memory = results.Get<MemoryDomainResult>();
        GCGenerationDomainResult? gcGen = results.Get<GCGenerationDomainResult>();
        ObjectShapeAnalyzerDomainResult? shape = results.Get<ObjectShapeAnalyzerDomainResult>();

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            T("Type shape and finalizable-type overhead. Retained size remains approximate unless the BFS-backed analysis is present."),
        };

        if (memory?.TopTypes is not { Count: > 0 })
        {
            blocks.Add(T("No memory top-type data was available."));
            return new AnalyzerDetailSection("TypeTable", DisplayTitle, SortOrder, blocks, SectionId, "TypeSystem");
        }

        // Generation profiles by type name, so the scan below is one pass over every type (the first profile of a name wins, as before).
        var generationByType = new Dictionary<string, TypeGenerationProfile>(StringComparer.Ordinal);
        if (gcGen?.PerTypeGenerationProfiles is { Count: > 0 })
        {
            for (int i = 0; i < gcGen.PerTypeGenerationProfiles.Count; i++)
                generationByType.TryAdd(gcGen.PerTypeGenerationProfiles[i].TypeName, gcGen.PerTypeGenerationProfiles[i]);
        }

        var finalizableOverheadRows = new List<FinalizableOverheadCandidate>();
        for (int i = 0; i < memory.TopTypes.Count; i++)
        {
            TypeSnapshot type = memory.TopTypes[i];
            if (!generationByType.TryGetValue(type.TypeName, out TypeGenerationProfile? gen) || !gen.IsFinalizable)
                continue;

            long totalCount = gen.Gen0Count + gen.Gen1Count + gen.Gen2Count + gen.LohCount;
            if (totalCount <= 0 || gen.TotalBytes <= 0)
                continue;

            ulong avgSize = gen.TotalBytes / (ulong)totalCount;
            ulong estimatedGen2Bytes = (ulong)gen.Gen2Count * avgSize;
            if (estimatedGen2Bytes > 0)
            {
                string moduleName = string.IsNullOrWhiteSpace(type.ModuleName) ? "N/A" : type.ModuleName;
                finalizableOverheadRows.Add(new FinalizableOverheadCandidate(type.TypeName, gen.Gen2Count, avgSize, estimatedGen2Bytes, moduleName));
            }
        }

        // Per-type size, generation and shape columns live in Memory Overview ("Top types") and GC Generation ("Per-type generation profiles"); this
        // section used to repeat the first 30 types with a join of those, which cut the population and repeated six columns.
        blocks.Add(T("Per-type size, retained size and module are in Memory Overview (Top types); per-type generation counts are in GC Generation Analysis (Per-type generation profiles)."));

        if (shape?.TopValueHeavyTypes is { Count: > 0 })
        {
            var valueRows = new List<TableRow>(shape.TopValueHeavyTypes.Count);
            for (int i = 0; i < shape.TopValueHeavyTypes.Count; i++)
            {
                TypeShapeProfile profile = shape.TopValueHeavyTypes[i];
                valueRows.Add(Row(
                    Cell(FormatHelper.TruncateString(profile.TypeName, 70)),
                    Cell(profile.TotalFields.ToString("N0"), profile.TotalFields),
                    Cell(profile.ReferenceFields.ToString("N0"), profile.ReferenceFields),
                    Cell(profile.ValueFields.ToString("N0"), profile.ValueFields),
                    Cell(profile.ReferenceFieldRatio.ToString("F2"), profile.ReferenceFieldRatio),
                    Cell(profile.InstanceCount.ToString("N0"), profile.InstanceCount > (ulong)long.MaxValue ? long.MaxValue : (long)profile.InstanceCount),
                    Cell(profile.IsValueType ? "Yes" : "No"),
                    Cell(profile.IsArray ? "Yes" : "No"),
                    Cell(profile.BaseTypeChainDepth.ToString("N0"), profile.BaseTypeChainDepth),
                    Cell(profile.InterfaceCount.ToString("N0"), profile.InterfaceCount),
                    Cell(profile.Category.ToString())));
            }
            compactTables.Add(STCompact(
                "Value-heavy types",
                new[] { CHType("Type"), CH("Total Fields","number"), CH("Reference Fields","number"), CH("Value Fields","number"), CH("Ref Ratio", "number", "ratio"), CH("Instances","number"), CH("Value Type"), CH("Array"), CH("Base Depth","number"), CH("Interfaces","number"), CH("Category") },
                valueRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        var treemapItems = new List<object>();
        int treemapLimit = Math.Min(memory.TopTypes.Count, 8);
        for (int i = 0; i < treemapLimit; i++)
        {
            TypeSnapshot type = memory.TopTypes[i];
            treemapItems.Add(new { label = type.TypeName, value = type.TotalBytes });
        }

        if (treemapItems.Count > 0)
        {
            blocks.Add(Chart(
                "Type treemap",
                "treemap",
                JsonSerializer.Serialize(new
                {
                    title = "Top types by shallow size",
                    subtitle = "Largest object types in the current snapshot",
                    items = treemapItems
                })));
        }


        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>();

        if (finalizableOverheadRows.Count > 0)
        {
            blocks.Add(H("FINALIZABLE GEN2 OVERHEAD"));
            blocks.Add(T("Estimated as Gen2 count multiplied by average shallow size for the finalizable types present in the current type table."));

            finalizableOverheadRows.Sort(static (a, b) => b.EstimatedGen2Bytes.CompareTo(a.EstimatedGen2Bytes));
            int finalizableLimit = finalizableOverheadRows.Count;
            var overheadRows = new List<TableRow>(finalizableLimit);
            ulong totalEstimatedBytes = 0;
            for (int i = 0; i < finalizableLimit; i++)
            {
                FinalizableOverheadCandidate candidate = finalizableOverheadRows[i];
                totalEstimatedBytes += candidate.EstimatedGen2Bytes;
                overheadRows.Add(Row(
                    Cell(candidate.TypeName),
                    Cell(candidate.Gen2Count.ToString("N0"), candidate.Gen2Count),
                    Cell(FormatBytes(candidate.AverageSize), (long)Math.Min(candidate.AverageSize, long.MaxValue)),
                    Cell(FormatBytes(candidate.EstimatedGen2Bytes), (long)Math.Min(candidate.EstimatedGen2Bytes, long.MaxValue)),
                    Cell(candidate.ModuleName)));
            }

            keyMetrics["estimated_finalizable_gen2_bytes"] = new NumericMetricValue((double)Math.Min(totalEstimatedBytes, long.MaxValue), MetricUnit.Bytes, FormatBytes(totalEstimatedBytes));
            compactTables.Add(STCompact(
                "Finalizable types by estimated Gen2 overhead",
                new[] { CHType("Type"), CH("Gen2 Count","number"), CH("Avg Size","bytes"), CH("Est. Gen2 Bytes","bytes"), CHModule("Module") },
                overheadRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

        }

        blocks.Add(H("TYPE SHAPE NOTES"));
        blocks.Add(T("Balanced shapes are surfaced as Mixed; arrays are flagged explicitly in the value-heavy types table."));

        return new AnalyzerDetailSection(
            "TypeTable", DisplayTitle, SortOrder, blocks, SectionId, "TypeSystem",
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private sealed record FinalizableOverheadCandidate(
        string TypeName,
        long Gen2Count,
        ulong AverageSize,
        ulong EstimatedGen2Bytes,
        string ModuleName);

    private static string FormatBytes(ulong value)
    {
        string[] units = new[] { "B", "KB", "MB", "GB", "TB" };
        double bytes = value;
        int unitIndex = 0;
        while (bytes >= 1024 && unitIndex < units.Length - 1)
        {
            bytes /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:N0} B" : $"{bytes:F1} {units[unitIndex]}";
    }
}