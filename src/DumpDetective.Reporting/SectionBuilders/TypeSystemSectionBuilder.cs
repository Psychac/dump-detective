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
    private const int TopRows = 30;


    public string SectionId => "C1";
    public string DisplayTitle => "Type Table";
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
        GCRootDomainResult? roots = results.Get<GCRootDomainResult>();

        var compactTables = new List<CompactTable>();
        var blocks = new List<SectionBlock>
        {
            T("Types are ranked by shallow size; retained size remains approximate unless the BFS-backed analysis is present."),
        };

        if (memory?.TopTypes is not { Count: > 0 })
        {
            blocks.Add(T("No memory top-type data was available."));
            return new AnalyzerDetailSection("TypeTable", DisplayTitle, SortOrder, blocks, SectionId, "TypeSystem");
        }

        var rows = new List<TableRow>(Math.Min(memory.TopTypes.Count, TopRows));
        var finalizableOverheadRows = new List<FinalizableOverheadCandidate>();
        int limit = Math.Min(memory.TopTypes.Count, TopRows);
        for (int i = 0; i < limit; i++)
        {
            TypeSnapshot type = memory.TopTypes[i];
            TypeShapeProfile? profile = FindShape(shape, type.TypeName);
            TypeGenerationProfile? gen = FindGeneration(gcGen, type.TypeName);
            string moduleName = string.IsNullOrWhiteSpace(type.ModuleName) ? "N/A" : type.ModuleName;

            rows.Add(Row(
                Cell(FormatHelper.TruncateString(type.TypeName, 70)),
                Cell(type.Count.ToString("N0"), type.Count),
                Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                Cell(type.LohBytes > 0 ? FormatBytes(type.LohBytes) : "—"),
                Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : (type.Count > 0 ? FormatBytes(type.TotalBytes / (ulong)type.Count) : "—")),
                Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—"),
                Cell(gen is null ? "—" : GenPct(gen, 0), gen is null ? null : GenPctValue(gen, 0)),
                Cell(gen is null ? "—" : GenPct(gen, 1), gen is null ? null : GenPctValue(gen, 1)),
                Cell(gen is null ? "—" : GenRatio(gen), gen is null ? null : GenRatioValue(gen)),
                Cell(profile is null ? "—" : profile.IsFinalizable ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.IsValueType ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.ReferenceFields.ToString("N0"), profile is null ? null : profile.ReferenceFields),
                Cell(profile is null ? "—" : profile.ReferenceFieldRatio.ToString("F2"), profile is null ? null : profile.ReferenceFieldRatio),
                Cell(profile is null ? "—" : profile.IsArray ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.BaseTypeChainDepth.ToString("N0"), profile is null ? null : profile.BaseTypeChainDepth),
                Cell(profile is null ? "—" : profile.InterfaceCount.ToString("N0"), profile is null ? null : profile.InterfaceCount),
                Cell(moduleName),
                Cell("N/A")));

            if (gen is not null && gen.IsFinalizable)
            {
                long totalCount = gen.Gen0Count + gen.Gen1Count + gen.Gen2Count + gen.LohCount;
                if (totalCount > 0 && gen.TotalBytes > 0)
                {
                    ulong avgSize = gen.TotalBytes / (ulong)totalCount;
                    ulong estimatedGen2Bytes = (ulong)gen.Gen2Count * avgSize;
                    if (estimatedGen2Bytes > 0)
                    {
                        finalizableOverheadRows.Add(new FinalizableOverheadCandidate(
                            type.TypeName,
                            gen.Gen2Count,
                            avgSize,
                            estimatedGen2Bytes,
                            moduleName));
                    }
                }
            }
        }

        compactTables.Add(STCompact(
            "Type table",
            new[] { CH("Type"), CH("Count","number"), CH("Shallow Size","bytes"), CH("LOH Bytes","bytes"), CH("Avg Size","bytes"), CH("Est. Retained","bytes"), CH("Gen0%", "number", "percent"), CH("Gen1%", "number", "percent"), CH("Gen2%", "number", "percent"), CH("Finalizable"), CH("Value Type"), CH("Ref Fields","number"), CH("Ref Ratio", "number", "ratio"), CH("Array"), CH("Base Depth","number"), CH("Interfaces","number"), CH("Module"), CH("Method Table") },
            rows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

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
                new[] { CH("Type"), CH("Total Fields","number"), CH("Reference Fields","number"), CH("Value Fields","number"), CH("Ref Ratio", "number", "ratio"), CH("Instances","number"), CH("Value Type"), CH("Array"), CH("Base Depth","number"), CH("Interfaces","number"), CH("Category") },
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

        if (memory.TopTypes.Count > TopRows)
            blocks.Add(T($"Showing top {TopRows} types by shallow size. {memory.TopTypes.Count - TopRows} additional type(s) omitted."));

        var keyMetrics = new System.Collections.Generic.Dictionary<string, MetricValue>();

        if (finalizableOverheadRows.Count > 0)
        {
            blocks.Add(H("FINALIZABLE GEN2 OVERHEAD"));
            blocks.Add(T("Estimated as Gen2 count multiplied by average shallow size for the finalizable types present in the current type table."));

            finalizableOverheadRows.Sort(static (a, b) => b.EstimatedGen2Bytes.CompareTo(a.EstimatedGen2Bytes));
            int finalizableLimit = Math.Min(finalizableOverheadRows.Count, 10);
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
                new[] { CH("Type"), CH("Gen2 Count","number"), CH("Avg Size","bytes"), CH("Est. Gen2 Bytes","bytes"), CH("Module") },
                overheadRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));

            if (finalizableOverheadRows.Count > finalizableLimit)
                blocks.Add(T($"Showing top {finalizableLimit} finalizable type(s). {finalizableOverheadRows.Count - finalizableLimit} additional type(s) omitted."));
        }

        blocks.Add(H("DOMINATOR CANDIDATES"));
        blocks.Add(T("Candidates are nominated from available type, generation, and shape signals."));

        var candidates = BuildCandidates(memory, gcGen, shape, roots);
        if (candidates.Count == 0)
        {
            blocks.Add(T("No dominator candidates met the heuristic thresholds."));
        }
        else
        {
            var candidateRows = new List<TableRow>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                DominatorCandidate candidate = candidates[i];
                candidateRows.Add(Row(
                    Cell(candidate.TypeName),
                    Cell(candidate.Reasons),
                    Cell(candidate.InstanceCount.ToString("N0"), candidate.InstanceCount),
                    Cell(FormatBytes(candidate.ShallowBytes), (long)Math.Min(candidate.ShallowBytes, long.MaxValue)),
                    Cell(candidate.Gen2Pct.ToString("F1") + "%"),
                    Cell(candidate.RetainedBytes > 0 ? FormatBytes(candidate.RetainedBytes) : "—"),
                    Cell(candidate.SampleAddress == 0 ? "—" : $"0x{candidate.SampleAddress:X}"),
                    Cell(candidate.Rooted ? "Rooted" : "Unknown")));
            }
            compactTables.Add(STCompact(
                "Dominator candidates",
                new[] { CH("Type"), CH("Reason"), CH("Instances","number"), CH("Shallow Size","bytes"), CH("Gen2%", "number", "percent"), CH("Estimated Retained","bytes"), CH("Sample Address"), CH("GC Root Reachability") },
                candidateRows.Select(r => R(r.Cells.Select(c => (object?)(c.RawValue ?? (object?)c.Display)).ToArray())).ToArray()));
        }

        blocks.Add(H("TYPE SHAPE NOTES"));
        blocks.Add(T("Balanced shapes are surfaced as Mixed; arrays are flagged explicitly in the type table."));

        return new AnalyzerDetailSection(
            "TypeTable", DisplayTitle, SortOrder, blocks, SectionId, "TypeSystem",
            KeyMetrics: keyMetrics,
            CompactTables: compactTables.Count > 0 ? compactTables : null);
    }

    private static TypeShapeProfile? FindShape(ObjectShapeAnalyzerDomainResult? shape, string typeName)
    {
        if (shape is null)
            return null;

        for (int i = 0; i < shape.TopReferenceHeavyTypes.Count; i++)
            if (string.Equals(shape.TopReferenceHeavyTypes[i].TypeName, typeName, StringComparison.Ordinal))
                return shape.TopReferenceHeavyTypes[i];

        for (int i = 0; i < shape.TopValueHeavyTypes.Count; i++)
            if (string.Equals(shape.TopValueHeavyTypes[i].TypeName, typeName, StringComparison.Ordinal))
                return shape.TopValueHeavyTypes[i];

        return null;
    }

    private static TypeGenerationProfile? FindGeneration(GCGenerationDomainResult? gcGen, string typeName)
    {
        if (gcGen?.PerTypeGenerationProfiles is not { Count: > 0 })
            return null;

        for (int i = 0; i < gcGen.PerTypeGenerationProfiles.Count; i++)
            if (string.Equals(gcGen.PerTypeGenerationProfiles[i].TypeName, typeName, StringComparison.Ordinal))
                return gcGen.PerTypeGenerationProfiles[i];

        return null;
    }

    private static double GenRatioValue(TypeGenerationProfile profile)
    {
        long total = profile.Gen0Count + profile.Gen1Count + profile.Gen2Count;
        if (total == 0)
            return 0.0;

        return profile.Gen2Count * 100.0 / total;
    }

    private static string GenRatio(TypeGenerationProfile profile)
        => $"{GenRatioValue(profile):F1}%";

    private static double GenPctValue(TypeGenerationProfile profile, int gen)
    {
        long total = profile.Gen0Count + profile.Gen1Count + profile.Gen2Count + profile.LohCount;
        if (total == 0)
            return 0.0;
        long count = gen == 0 ? profile.Gen0Count : gen == 1 ? profile.Gen1Count : profile.Gen2Count;
        return count * 100.0 / total;
    }

    private static string GenPct(TypeGenerationProfile profile, int gen)
        => $"{GenPctValue(profile, gen):F1}%";

    private sealed record DominatorCandidate(
        string TypeName,
        string Reasons,
        int InstanceCount,
        ulong ShallowBytes,
        double Gen2Pct,
        ulong RetainedBytes,
        ulong SampleAddress,
        bool Rooted);

    private sealed record FinalizableOverheadCandidate(
        string TypeName,
        long Gen2Count,
        ulong AverageSize,
        ulong EstimatedGen2Bytes,
        string ModuleName);

    private static IReadOnlyList<DominatorCandidate> BuildCandidates(MemoryDomainResult memory, GCGenerationDomainResult? gcGen, ObjectShapeAnalyzerDomainResult? shape, GCRootDomainResult? roots)
    {
        var candidates = new List<DominatorCandidate>();
        ulong heapTotal = memory.TotalBytes == 0 ? 1 : memory.TotalBytes;
        var rootedTypes = new HashSet<string>(StringComparer.Ordinal);
        if (roots is not null)
        {
            for (int i = 0; i < roots.TopRootsBySeverity.Count; i++)
                rootedTypes.Add(roots.TopRootsBySeverity[i].TargetTypeName);

            for (int i = 0; i < roots.RootPaths.Count; i++)
                rootedTypes.Add(roots.RootPaths[i].TargetTypeName);
        }

        int limit = Math.Min(memory.TopTypes.Count, TopRows);
        for (int i = 0; i < limit; i++)
        {
            TypeSnapshot type = memory.TopTypes[i];
            TypeGenerationProfile? gen = FindGeneration(gcGen, type.TypeName);
            TypeShapeProfile? profile = FindShape(shape, type.TypeName);

            double gen2Pct = gen is null ? 0.0 : GenRatioValue(gen);
            bool finalizable = profile?.IsFinalizable ?? false;
            bool rooted = rootedTypes.Contains(type.TypeName) || type.EstimatedRetainedBytes > 0;
            var reasons = new List<string>();

            if ((double)type.TotalBytes / heapTotal > 0.01)
                reasons.Add(">1% of heap");
            if (gen2Pct > 80.0)
                reasons.Add("Gen2-heavy");
            if (finalizable && type.Count > 500)
                reasons.Add("finalizable");
            if (type.TotalBytes > 50UL * 1024 * 1024)
                reasons.Add("large footprint");

            if (reasons.Count == 0)
                continue;

            candidates.Add(new DominatorCandidate(
                type.TypeName,
                string.Join(", ", reasons),
                type.Count,
                type.TotalBytes,
                gen2Pct,
                type.EstimatedRetainedBytes,
                type.SampleAddress,
                rooted));
        }

        return candidates.OrderByDescending(c => c.ShallowBytes).Take(TopRows).ToArray();
    }

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