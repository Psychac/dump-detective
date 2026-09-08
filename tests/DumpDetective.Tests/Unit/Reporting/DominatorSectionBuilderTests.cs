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
    private static readonly IReadOnlyList<TypeSnapshot> DefaultTopDominatorTypes =
    [
        new TypeSnapshot(
            TypeName: "App.LeakyType",
            Count: 5,
            TotalBytes: 500,
            LohBytes: 200,
            EstimatedRetainedBytes: 1_000,
            SampleAddress: 0x1000,
            Gen2Count: 3)
    ];

    private static DominatorDomainResult BuildResult(
        IReadOnlyDictionary<string, ulong>? exactByType,
        IReadOnlyDictionary<string, IReadOnlyList<DominatorChainHop>>? chainsByType = null,
        IReadOnlyDictionary<string, string>? containingTypeNameByType = null,
        IReadOnlyList<CrossTypeOverlapPair>? crossTypeOverlapPairs = null,
        bool crossTypeOverlapInstanceScanCapped = false,
        IReadOnlyDictionary<string, RootChainSummary>? rootChainsByType = null,
        IReadOnlyList<TypeSnapshot>? topDominatorTypes = null) =>
        new(
            CandidateCount: 1,
            AnalyzedCount: 1,
            TotalEstimatedRetainedBytes: 1_000,
            TopDominatorTypes: topDominatorTypes ?? DefaultTopDominatorTypes,
            ExactRetainedBytesByTypeName: exactByType,
            DominatorChainsByTypeName: chainsByType,
            ContainingTypeNameByTypeName: containingTypeNameByType,
            CrossTypeOverlapPairs: crossTypeOverlapPairs,
            CrossTypeOverlapInstanceScanCapped: crossTypeOverlapInstanceScanCapped,
            RootChainsByTypeName: rootChainsByType);

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
    public void Build_TopGen2LohCandidate_RendersRetainedShallowInterpretation()
    {
        // Default candidate: EstimatedRetainedBytes 1_000, TotalBytes 500 -> ratio 2.0, the
        // "medium" (retained > shallow) tier.
        DominatorDomainResult result = BuildResult(exactByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        InterpretationBlock interpretation = section.Blocks.OfType<InterpretationBlock>().Single();
        interpretation.Text.Should().Contain("App.LeakyType");
        interpretation.Text.Should().Contain("retained > shallow");
    }

    [Fact]
    public void Build_TopGen2LohCandidate_SelfContainedRatio_RendersLowestTier()
    {
        var topTypes = new[]
        {
            new TypeSnapshot(TypeName: "App.SelfContained", Count: 1, TotalBytes: 1_000, LohBytes: 0, EstimatedRetainedBytes: 1_050, SampleAddress: 0x1, Gen2Count: 1),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        InterpretationBlock interpretation = section.Blocks.OfType<InterpretationBlock>().Single();
        interpretation.Text.Should().Contain("retained ≈ shallow");
    }

    [Fact]
    public void Build_TopGen2LohCandidate_LargeExternalGraphRatio_RendersHighestTier()
    {
        var topTypes = new[]
        {
            new TypeSnapshot(TypeName: "App.BigHolder", Count: 1, TotalBytes: 100, LohBytes: 0, EstimatedRetainedBytes: 1_000_000, SampleAddress: 0x1, Gen2Count: 1),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        InterpretationBlock interpretation = section.Blocks.OfType<InterpretationBlock>().Single();
        interpretation.Text.Should().Contain("retained ≫ shallow");
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

    [Fact]
    public void Build_NoRootChainData_OmitsRootPathsWidget()
    {
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        (section.TreeWidgets ?? []).Should().NotContain(w => w.Title == "Gen2 / LOH root paths");
    }

    [Fact]
    public void Build_RootChainDataForGen2LohType_RendersNestedRootPathTreeWidget()
    {
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["App.LeakyType"] = new RootChainSummary("Static", ["App.StaticHolder", "App.LeakyType"], Truncated: false),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths");
        TreeNode root = widget.Roots.Single();
        root.Label.Should().Be("[Static] App.StaticHolder");
        root.IsChain.Should().BeTrue();
        root.Children.Should().ContainSingle();
        root.Children!.Single().Label.Should().Be("App.LeakyType");
        root.Children!.Single().Children.Should().BeNull();
    }

    [Fact]
    public void Build_RootChainTruncated_LabelNotesTruncation()
    {
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["App.LeakyType"] = new RootChainSummary("Stack", ["App.LeakyType"], Truncated: true),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeNode root = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths").Roots.Single();
        root.Label.Should().Contain("[Stack] App.LeakyType");
        root.Label.Should().Contain("search truncated");
    }

    [Fact]
    public void Build_RootChainDataForTypeOutsideGen2Loh_NotRendered()
    {
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["Some.NonGen2LohType"] = new RootChainSummary("Static", ["Some.NonGen2LohType"], Truncated: false),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        (section.TreeWidgets ?? []).Should().NotContain(w => w.Title == "Gen2 / LOH root paths");
    }

    private static TypeSnapshot MakeGen2Candidate(string typeName, ulong sampleAddress) =>
        new(TypeName: typeName, Count: 1, TotalBytes: 100, LohBytes: 0, EstimatedRetainedBytes: 100, SampleAddress: sampleAddress, Gen2Count: 1);

    [Fact]
    public void Build_TwoTypesShareIdenticalChainShape_DedupedIntoOneTreeWithBothNamesInLeaf()
    {
        var topTypes = new[] { MakeGen2Candidate("App.TypeA", 0x1), MakeGen2Candidate("App.TypeB", 0x2) };
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["App.TypeA"] = new RootChainSummary("Static", ["App.StaticCache", "App.Bucket", "App.TypeA"], Truncated: false),
            ["App.TypeB"] = new RootChainSummary("Static", ["App.StaticCache", "App.Bucket", "App.TypeB"], Truncated: false),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths");
        widget.Roots.Should().ContainSingle("both candidates share the same ancestor chain shape");
        TreeNode root = widget.Roots.Single();
        root.Label.Should().Be("[Static] App.StaticCache");
        TreeNode middle = root.Children!.Single();
        middle.Label.Should().Be("App.Bucket");
        TreeNode leaf = middle.Children!.Single();
        leaf.Label.Should().Be("×2 types — same chain: App.TypeA, App.TypeB");
        leaf.Children.Should().BeNull();
    }

    [Fact]
    public void Build_DifferentChainShapes_NotDeduped_RendersSeparateTrees()
    {
        var topTypes = new[] { MakeGen2Candidate("App.TypeA", 0x1), MakeGen2Candidate("App.TypeB", 0x2) };
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["App.TypeA"] = new RootChainSummary("Static", ["App.StaticCache", "App.TypeA"], Truncated: false),
            ["App.TypeB"] = new RootChainSummary("Stack", ["App.LocalVar", "App.TypeB"], Truncated: false),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths");
        widget.Roots.Should().HaveCount(2, "the two chains have different root kinds and ancestor hops");
    }

    [Fact]
    public void Build_DirectRootChains_NeverDedupedEvenWithSameRootKind()
    {
        // Both are direct GC roots (single-hop chains) of the same kind — sharing only a root kind
        // label, not an actual ancestor path, so they must never be collapsed together.
        var topTypes = new[] { MakeGen2Candidate("App.TypeA", 0x1), MakeGen2Candidate("App.TypeB", 0x2) };
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal)
        {
            ["App.TypeA"] = new RootChainSummary("Static", ["App.TypeA"], Truncated: false),
            ["App.TypeB"] = new RootChainSummary("Static", ["App.TypeB"], Truncated: false),
        };
        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths");
        widget.Roots.Should().HaveCount(2);
    }

    [Fact]
    public void Build_ManySharedChainTypes_LeafLabelCapsNamesWithMoreSuffix()
    {
        var topTypes = new[]
        {
            MakeGen2Candidate("App.Type1", 0x1), MakeGen2Candidate("App.Type2", 0x2),
            MakeGen2Candidate("App.Type3", 0x3), MakeGen2Candidate("App.Type4", 0x4),
            MakeGen2Candidate("App.Type5", 0x5), MakeGen2Candidate("App.Type6", 0x6),
            MakeGen2Candidate("App.Type7", 0x7),
        };
        var rootChainsByType = new Dictionary<string, RootChainSummary>(StringComparer.Ordinal);
        foreach (TypeSnapshot type in topTypes)
            rootChainsByType[type.TypeName] = new RootChainSummary("Static", ["App.StaticCache", type.TypeName], Truncated: false);

        DominatorDomainResult result = BuildResult(exactByType: null, rootChainsByType: rootChainsByType, topDominatorTypes: topTypes);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        TreeWidget widget = section.TreeWidgets!.Single(w => w.Title == "Gen2 / LOH root paths");
        TreeNode leaf = widget.Roots.Single().Children!.Single();
        leaf.Label.Should().Be("×7 types — same chain: App.Type1, App.Type2, App.Type3, App.Type4, App.Type5, +2 more");
    }

    [Fact]
    public void Build_NoContainmentData_OmitsOverlapTable()
    {
        DominatorDomainResult result = BuildResult(exactByType: null, containingTypeNameByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.CompactTables!.Should().NotContain(t => t.Title == "Shared subgraph overlap (sample-based)");
    }

    [Fact]
    public void Build_ContainmentDataForGen2LohType_RendersOverlapTable()
    {
        var containingTypeNameByType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App.LeakyType"] = "App.StaticCache",
        };
        DominatorDomainResult result = BuildResult(exactByType: null, containingTypeNameByType: containingTypeNameByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = section.CompactTables!.Single(t => t.Title == "Shared subgraph overlap (sample-based)");
        int containedWithinColumn = table.Headers.ToList().FindIndex(h => h.Name == "Fully contained within");
        table.Rows.Single().Values[0].Should().Be("App.LeakyType");
        table.Rows.Single().Values[containedWithinColumn].Should().Be("App.StaticCache");
    }

    [Fact]
    public void Build_ContainmentDataForTypeOutsideGen2Loh_NotRendered()
    {
        var containingTypeNameByType = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Some.NonGen2LohType"] = "App.StaticCache",
        };
        DominatorDomainResult result = BuildResult(exactByType: null, containingTypeNameByType: containingTypeNameByType);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.CompactTables!.Should().NotContain(t => t.Title == "Shared subgraph overlap (sample-based)");
    }

    [Fact]
    public void Build_NoOverlapPairs_OmitsPopulationOverlapTable()
    {
        DominatorDomainResult result = BuildResult(exactByType: null, crossTypeOverlapPairs: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.CompactTables!.Should().NotContain(t => t.Title == "Cross-type retained overlap");
    }

    [Fact]
    public void Build_OverlapPairForGen2LohType_RendersPopulationOverlapTable()
    {
        var pairs = new List<CrossTypeOverlapPair> { new("App.LeakyType", "App.StaticCache", 42, ContainedRetainedBytes: 5_000) };
        DominatorDomainResult result = BuildResult(exactByType: null, crossTypeOverlapPairs: pairs);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = section.CompactTables!.Single(t => t.Title == "Cross-type retained overlap");
        int typeColumn = table.Headers.ToList().FindIndex(h => h.Name == "Type");
        int containedWithinColumn = table.Headers.ToList().FindIndex(h => h.Name == "Contained within");
        int instancesColumn = table.Headers.ToList().FindIndex(h => h.Name == "Instances");
        int retainedColumn = table.Headers.ToList().FindIndex(h => h.Name == "Retained");
        table.Rows.Single().Values[typeColumn].Should().Be("App.LeakyType");
        table.Rows.Single().Values[containedWithinColumn].Should().Be("App.StaticCache");
        table.Rows.Single().Values[instancesColumn].Should().Be(42);
        table.Rows.Single().Values[retainedColumn].Should().Be(5_000UL);
    }

    [Fact]
    public void Build_OverlapPairWithZeroRetainedBytes_RendersNullNotZero()
    {
        // ContainedRetainedBytes == 0 is a real, honest outcome (every contained instance happened
        // to be non-topmost) — should render as an absent cell like other "not computed" values in
        // this section, not a misleading literal 0.
        var pairs = new List<CrossTypeOverlapPair> { new("App.LeakyType", "App.StaticCache", 3, ContainedRetainedBytes: 0) };
        DominatorDomainResult result = BuildResult(exactByType: null, crossTypeOverlapPairs: pairs);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        CompactTable table = section.CompactTables!.Single(t => t.Title == "Cross-type retained overlap");
        int retainedColumn = table.Headers.ToList().FindIndex(h => h.Name == "Retained");
        table.Rows.Single().Values[retainedColumn].Should().BeNull();
    }

    [Fact]
    public void Build_OverlapPairForTypeOutsideGen2Loh_NotRendered()
    {
        var pairs = new List<CrossTypeOverlapPair> { new("Some.NonGen2LohType", "App.StaticCache", 7) };
        DominatorDomainResult result = BuildResult(exactByType: null, crossTypeOverlapPairs: pairs);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        section.CompactTables!.Should().NotContain(t => t.Title == "Cross-type retained overlap");
    }

    [Fact]
    public void Build_OverlapInstanceScanCapped_AddsCaveatToConfidenceBand()
    {
        DominatorDomainResult result = BuildResult(exactByType: null, crossTypeOverlapInstanceScanCapped: true);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        ConfidenceBandBlock band = section.Blocks.OfType<ConfidenceBandBlock>().Single();
        band.Caveats.Should().Contain(c => c.Contains("Cross-type overlap instance scan was capped"));
    }

    [Fact]
    public void Build_AlwaysRendersNextStepsPointingAtGCRootAndReferenceChainAnalysis()
    {
        DominatorDomainResult result = BuildResult(exactByType: null);

        AnalyzerDetailSection section = new DominatorSectionBuilder().Build(result);

        NextStepsBlock nextSteps = section.Blocks.OfType<NextStepsBlock>().Single();
        nextSteps.Links.Should().Contain(l => l.SectionId == "A5"); // GCRootAnalyzer
        nextSteps.Links.Should().Contain(l => l.SectionId == "A4"); // ReferenceChainAnalyzer
    }
}
