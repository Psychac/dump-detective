using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Abstractions;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class TypeSystemSectionBuilderTests
{
    private static AnalyzerRunResult Run(string analyzer, AnalyzerDomainResult result) =>
        new(analyzer, AnalyzerExecutionStatus.Success, TimeSpan.FromMilliseconds(1), result, null, null, Findings: []);

    private static AnalyzerDetailSection Build(int typeCount, int finalizableIndex)
    {
        var types = new List<TypeSnapshot>();
        var profiles = new List<TypeGenerationProfile>();
        for (int i = 0; i < typeCount; i++)
        {
            types.Add(new TypeSnapshot($"Demo.Type{i}", 10, 1024, 0, 102, 0, 0, "Demo.Module"));
            profiles.Add(new TypeGenerationProfile($"Demo.Type{i}", 1, 2, 7, 0, 1024, IsFinalizable: i == finalizableIndex, Gen2Bytes: 700));
        }

        var memory = new MemoryDomainResult(
            TotalBytes: 1024, LohBytes: 0, LohPercent: 0, TotalObjects: 10, LohObjects: 0, LohThresholdBytes: 85000,
            UniqueTypes: typeCount, TopTypes: types, SizeBucketHistogram: []);
        var gcGen = new GCGenerationDomainResult(
            Gen0Bytes: 0, Gen0Objects: 0, Gen1Bytes: 0, Gen1Objects: 0, Gen2Bytes: 0, Gen2Objects: 0, LohBytes: 0, LohPercent: 0,
            TotalObjects: 0, LohObjects: 0, TopLohTypes: [], Gen2Pct: 100, PerTypeGenerationProfiles: profiles);

        var set = new AnalyzerResultSet([Run("MemoryAnalyzer", memory), Run("GCGenerationAnalyzer", gcGen)]);
        return new TypeSystemSectionBuilder().Build(set);
    }

    [Fact]
    public void Build_NoLongerEmitsTheThirtyRowTypeTable()
    {
        AnalyzerDetailSection section = Build(typeCount: 45, finalizableIndex: -1);

        section.CompactTables.Should().BeNull();
        section.Blocks.OfType<TextBlock>().Should().Contain(b => b.Text.Contains("Memory Overview (Top types)"));
    }

    [Fact]
    public void Build_FinalizableTypeBeyondTheFirstThirty_IsStillCounted()
    {
        AnalyzerDetailSection section = Build(typeCount: 45, finalizableIndex: 40);

        CompactTable table = section.CompactTables!.Should().ContainSingle(t => t.Title == "Finalizable types by estimated Gen2 overhead").Subject;
        table.Rows.Should().ContainSingle();
        section.KeyMetrics.Should().ContainKey("estimated_finalizable_gen2_bytes");
    }

    [Fact]
    public void Build_ManyFinalizableTypes_ShipsAllOfThem()
    {
        var types = new List<TypeSnapshot>();
        var profiles = new List<TypeGenerationProfile>();
        for (int i = 0; i < 25; i++)
        {
            types.Add(new TypeSnapshot($"Demo.Type{i}", 10, 1024, 0, 102, 0, 0, "Demo.Module"));
            profiles.Add(new TypeGenerationProfile($"Demo.Type{i}", 1, 2, 7, 0, 1024, IsFinalizable: true, Gen2Bytes: 700));
        }

        var memory = new MemoryDomainResult(1024, 0, 0, 10, 0, 85000, 25, types, []);
        var gcGen = new GCGenerationDomainResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], 100, PerTypeGenerationProfiles: profiles);
        AnalyzerDetailSection section = new TypeSystemSectionBuilder().Build(new AnalyzerResultSet([Run("MemoryAnalyzer", memory), Run("GCGenerationAnalyzer", gcGen)]));

        section.CompactTables!.Single(t => t.Title == "Finalizable types by estimated Gen2 overhead").Rows.Should().HaveCount(25);
    }
}
