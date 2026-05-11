using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Core.Utilities;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Reporting.SectionBuilders;

internal sealed class TypeSystemSectionBuilder : SectionBuilderBase, IReportSectionBuilder
{
    private const int TopRows = 30;

    public string SectionId => "prof.type-system";
    public string DisplayTitle => "Type System";
    public int SortOrder => 1100;

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

        var blocks = new List<SectionBlock>
        {
            H("TYPE TABLE"),
            T("Types are ranked by shallow size; retained size remains approximate unless the BFS-backed analysis is present."),
        };

        if (memory?.TopTypesBySize is not { Count: > 0 })
        {
            blocks.Add(T("No memory top-type data was available."));
            return new AnalyzerDetailSection("Type System", DisplayTitle, SortOrder, blocks);
        }

        var rows = new List<TableRow>(Math.Min(memory.TopTypesBySize.Count, TopRows));
        int limit = Math.Min(memory.TopTypesBySize.Count, TopRows);
        for (int i = 0; i < limit; i++)
        {
            TypeSnapshot type = memory.TopTypesBySize[i];
            TypeShapeProfile? profile = FindShape(shape, type.TypeName);
            TypeGenerationProfile? gen = FindGeneration(gcGen, type.TypeName);
            string moduleName = string.IsNullOrWhiteSpace(type.ModuleName) ? "N/A" : type.ModuleName;

            rows.Add(Row(
                Cell(FormatHelper.TruncateString(type.TypeName, 70)),
                Cell(type.Count.ToString("N0"), type.Count),
                Cell(FormatBytes(type.TotalBytes), (long)Math.Min(type.TotalBytes, long.MaxValue)),
                Cell(type.AverageSize > 0 ? FormatBytes(type.AverageSize) : (type.Count > 0 ? FormatBytes(type.TotalBytes / (ulong)type.Count) : "—")),
                Cell(type.EstimatedRetainedBytes > 0 ? FormatBytes(type.EstimatedRetainedBytes) : "—"),
                Cell(gen is null ? "—" : GenRatio(gen)),
                Cell(profile is null ? "—" : profile.IsFinalizable ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.IsValueType ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.ReferenceFields.ToString("N0"), profile is null ? null : profile.ReferenceFields),
                Cell(profile is null ? "—" : profile.IsArray ? "Yes" : "No"),
                Cell(profile is null ? "—" : profile.BaseTypeChainDepth.ToString("N0"), profile is null ? null : profile.BaseTypeChainDepth),
                Cell(profile is null ? "—" : profile.InterfaceCount.ToString("N0"), profile is null ? null : profile.InterfaceCount),
                Cell(moduleName),
                Cell("N/A")));
        }

        blocks.Add(new TableBlock(
            Caption: "Type table",
            Headers: ["Type", "Count", "Shallow Size", "Avg Size", "Estimated Retained", "Gen2%", "Finalizable", "Value Type", "Ref Fields", "Array", "Base Depth", "Interfaces", "Module", "Method Table"],
            Rows: rows));

        if (memory.TopTypesBySize.Count > TopRows)
            blocks.Add(T($"Showing top {TopRows} types by shallow size. {memory.TopTypesBySize.Count - TopRows} additional type(s) omitted."));

        blocks.Add(Blank());
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

            blocks.Add(new TableBlock(
                Caption: "Dominator candidates",
                Headers: ["Type", "Reason", "Instances", "Shallow Size", "Gen2%", "Estimated Retained", "Sample Address", "GC Root Reachability"],
                Rows: candidateRows));
        }

        blocks.Add(Blank());
        blocks.Add(H("TYPE SHAPE NOTES"));
        blocks.Add(T("Balanced shapes are surfaced as Mixed; arrays are flagged explicitly in the type table."));

        return new AnalyzerDetailSection("Type System", DisplayTitle, SortOrder, blocks);
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

    private static string GenRatio(TypeGenerationProfile profile)
    {
        int total = profile.Gen0Count + profile.Gen1Count + profile.Gen2Count;
        if (total == 0)
            return "—";

        return $"{profile.Gen2Count * 100.0 / total:F1}%";
    }

    private sealed record DominatorCandidate(
        string TypeName,
        string Reasons,
        int InstanceCount,
        ulong ShallowBytes,
        double Gen2Pct,
        ulong RetainedBytes,
        ulong SampleAddress,
        bool Rooted);

    private static List<DominatorCandidate> BuildCandidates(MemoryDomainResult memory, GCGenerationDomainResult? gcGen, ObjectShapeAnalyzerDomainResult? shape, GCRootDomainResult? roots)
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

        int limit = Math.Min(memory.TopTypesBySize.Count, TopRows);
        for (int i = 0; i < limit; i++)
        {
            TypeSnapshot type = memory.TopTypesBySize[i];
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

        return candidates.OrderByDescending(c => c.ShallowBytes).Take(TopRows).ToList();
    }

    private static double GenRatioValue(TypeGenerationProfile profile)
    {
        int total = profile.Gen0Count + profile.Gen1Count + profile.Gen2Count;
        return total == 0 ? 0.0 : profile.Gen2Count * 100.0 / total;
    }

    private static string FormatBytes(ulong value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
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