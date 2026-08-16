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
    private static DominatorDomainResult BuildResult(IReadOnlyDictionary<string, ulong>? exactByType) =>
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
            ExactRetainedBytesByTypeName: exactByType);

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
}
