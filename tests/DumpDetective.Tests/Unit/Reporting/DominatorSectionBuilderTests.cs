using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// §Report integration (docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md
/// §Architecture "Output model"): the Gen2/LOH sub-table's "Retained" column should use
/// <see cref="DominatorDomainResult.ExactRetainedBytesByTypeName"/> when present for a given type,
/// and fall back to <see cref="TypeSnapshot.EstimatedRetainedBytes"/> otherwise — every other table
/// in this section is untouched by that field.
/// </summary>
public sealed class DominatorSectionBuilderTests
{
    private static DominatorDomainResult BuildResult(
        IReadOnlyDictionary<string, ulong>? exactByType,
        IReadOnlyDictionary<string, IReadOnlyList<DominatorChainHop>>? chainsByType = null) =>
        new(
            CandidateCount: 1,
            AnalyzedCount: 1,
            TotalEstimatedRetainedBytes: 1_000,
            TopDominatorTypes:
            [
                new TypeSnapshot(
                    TypeName: "App.LeakyType",
                    Count: 5,
                    TotalBytes: 500,
                    LohBytes: 200,
                    EstimatedRetainedBytes: 1_000,
                    SampleAddress: 0x1000,
                    Gen2Count: 3)
            ],
            ExactRetainedBytesByTypeName: exactByType,
            DominatorChainsByTypeName: chainsByType);

    private static CompactTable GetGen2LohTable(AnalyzerDetailSection section) =>
        section.CompactTables!.Single(t => t.Title == "Gen2 / LOH dominator suspects");

    [Fact]
    public void Build_NoExactData_UsesHeuristicEstimatedRetainedBytes()
    {
        DominatorDomainResult result = BuildResult(exactByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = GetGen2LohTable(section);
        int retainedColumn = table.Headers.ToList().FindIndex(h => h.Name == "Retained");
        table.Rows.Single().Values[retainedColumn].Should().Be(1_000UL);
    }

    [Fact]
    public void Build_ExactDataForType_OverridesHeuristicRetainedBytes()
    {
        var exactByType = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["App.LeakyType"] = 5_000 };
        DominatorDomainResult result = BuildResult(exactByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = GetGen2LohTable(section);
        int retainedColumn = table.Headers.ToList().FindIndex(h => h.Name == "Retained");
        table.Rows.Single().Values[retainedColumn].Should().Be(5_000UL);
    }

    [Fact]
    public void Build_ExactDataForDifferentType_FallsBackToHeuristicForUnmatchedType()
    {
        var exactByType = new Dictionary<string, ulong>(StringComparer.Ordinal) { ["Some.OtherType"] = 5_000 };
        DominatorDomainResult result = BuildResult(exactByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = GetGen2LohTable(section);
        int retainedColumn = table.Headers.ToList().FindIndex(h => h.Name == "Retained");
        table.Rows.Single().Values[retainedColumn].Should().Be(1_000UL);
    }

    [Fact]
    public void Build_NoChainData_OmitsTreeWidgets()
    {
        DominatorDomainResult result = BuildResult(exactByType: null, chainsByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.TreeWidgets.Should().BeNull();
    }

    [Fact]
    public void Build_ChainDataForGen2LohType_RendersNestedChainTreeWidget()
    {
        var chainsByType = new Dictionary<string, IReadOnlyList<DominatorChainHop>>(StringComparer.Ordinal)
        {
            ["App.LeakyType"] =
            [
                new DominatorChainHop("App.StaticCache", 0x100, 5_000),
                new DominatorChainHop("App.LeakyType", 0x1000, 1_000),
            ],
        };
        DominatorDomainResult result = BuildResult(exactByType: null, chainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH dominance chains");
        TreeNode root = widget.Roots.Single();
        root.Label.Should().Contain("App.StaticCache");
        root.IsChain.Should().BeTrue();
        root.Children.Should().ContainSingle();
        root.Children!.Single().Label.Should().Contain("App.LeakyType");
        root.Children!.Single().Children.Should().BeNull();
    }

    [Fact]
    public void Build_ChainWithSentinelHop_SentinelLabelHasNoRetainedSuffix()
    {
        var chainsByType = new Dictionary<string, IReadOnlyList<DominatorChainHop>>(StringComparer.Ordinal)
        {
            ["App.LeakyType"] =
            [
                new DominatorChainHop("… chain continues beyond 64 hops", 0, 0),
                new DominatorChainHop("App.LeakyType", 0x1000, 1_000),
            ],
        };
        DominatorDomainResult result = BuildResult(exactByType: null, chainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeNode root = section.TreeWidgets!.Single().Roots.Single();
        root.Label.Should().Be("… chain continues beyond 64 hops");
        root.Label.Should().NotContain("retained");
    }

    [Fact]
    public void Build_ChainDataForTypeOutsideGen2Loh_NotRendered()
    {
        var chainsByType = new Dictionary<string, IReadOnlyList<DominatorChainHop>>(StringComparer.Ordinal)
        {
            ["Some.NonGen2LohType"] = [new DominatorChainHop("Some.NonGen2LohType", 0x2000, 300)],
        };
        DominatorDomainResult result = BuildResult(exactByType: null, chainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.TreeWidgets.Should().BeNull();
    }
}
