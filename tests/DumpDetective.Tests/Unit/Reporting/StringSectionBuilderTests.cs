using System.Linq;

using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

/// <summary>
/// P3-1: <see cref="StringSectionBuilder"/> groups exact-duplicate patterns that share a long
/// common prefix (templated/formatted strings differing only in a trailing id/timestamp) into a
/// "String prefix clusters" table. P3-3: the section's confidence band score falls dynamically
/// as <see cref="StringDomainResult.SamplingCoverage"/> falls.
/// </summary>
public sealed class StringSectionBuilderTests
{
    private static StringDomainResult BuildResult(
        IReadOnlyList<DuplicateStringSnapshot> topDuplicates,
        double samplingCoverage = 1.0,
        IReadOnlyList<DuplicateStringRetentionPath>? retentionPaths = null) => new(
        TotalStrings: 1_000,
        TotalStringMemoryBytes: 100_000,
        SampledUniquePatterns: 100,
        DuplicatePatternCount: topDuplicates.Count,
        DuplicateWastedBytes: (ulong)topDuplicates.Sum(d => (long)d.WastedBytes),
        DuplicationRatio: 0.5,
        PctOfManagedHeap: 10.0,
        TopDuplicates: topDuplicates,
        VeryLongStrings: [],
        LohStringBytes: 0,
        InternedStringCount: 0,
        InternedStringBytes: 0,
        Gen0StringCount: 0,
        Gen1StringCount: 0,
        Gen2StringCount: 0,
        Gen2StringBytes: 0,
        StringsSampled: 1_000,
        SamplingCoverage: samplingCoverage,
        TopDuplicateRetentionPaths: retentionPaths);

    private static CompactTable? FindClusterTable(AnalyzerDetailSection section) =>
        section.CompactTables?.FirstOrDefault(t => t.Title == "String prefix clusters");

    private static CompactTable? FindRetentionPathTable(AnalyzerDetailSection section) =>
        section.CompactTables?.FirstOrDefault(t => t.Title == "Duplicate string retention paths");

    private static ConfidenceBandBlock ConfidenceBand(AnalyzerDetailSection section) =>
        section.Blocks.OfType<ConfidenceBandBlock>().Single();

    [Fact]
    public void Build_GroupsPatterns_SharingLongCommonPrefix()
    {
        StringDomainResult result = BuildResult([
            new DuplicateStringSnapshot("OrderId=100001", 50, 5_000),
            new DuplicateStringSnapshot("OrderId=100002", 30, 3_000),
            new DuplicateStringSnapshot("OrderId=100003", 20, 2_000),
        ]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        CompactTable? table = FindClusterTable(section);
        table.Should().NotBeNull();
        table!.Rows.Should().ContainSingle();
        object?[] row = table.Rows[0].Values;
        row[0].Should().Be("OrderId=10000");
        row[1].Should().Be(3);   // distinct patterns
        row[2].Should().Be(100); // total occurrences
        row[3].Should().Be(10_000L); // total wasted bytes
    }

    [Fact]
    public void Build_DoesNotClusterPatterns_WithShortSharedPrefix()
    {
        StringDomainResult result = BuildResult([
            new DuplicateStringSnapshot("connection-string-template", 50, 5_000),
            new DuplicateStringSnapshot("cache-entry-key-value", 30, 3_000),
        ]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        FindClusterTable(section).Should().BeNull();
    }

    [Fact]
    public void Build_DoesNotClusterSinglePattern()
    {
        StringDomainResult result = BuildResult([
            new DuplicateStringSnapshot("OrderId=100001", 50, 5_000),
        ]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        FindClusterTable(section).Should().BeNull();
    }

    [Fact]
    public void Build_NoClusterTable_WhenNoDuplicates()
    {
        StringDomainResult result = BuildResult([]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        FindClusterTable(section).Should().BeNull();
    }

    [Fact]
    public void Build_FullConfidence_WhenSamplingCoverageIsHigh()
    {
        StringDomainResult result = BuildResult([], samplingCoverage: 1.0);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        ConfidenceBandBlock band = ConfidenceBand(section);
        band.Score.Should().Be(0.85);
        band.Band.Should().Be("High");
        band.Caveats.Should().NotContain(c => c.Contains("Sampling coverage"));
    }

    [Fact]
    public void Build_ReducedConfidence_WhenSamplingCoverageIsModerate()
    {
        StringDomainResult result = BuildResult([], samplingCoverage: 0.20);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        ConfidenceBandBlock band = ConfidenceBand(section);
        band.Score.Should().Be(0.70);
        band.Caveats.Should().Contain(c => c.Contains("Sampling coverage is 20.0%"));
    }

    [Fact]
    public void Build_LowConfidence_WhenSamplingCoverageIsBelowFivePercent()
    {
        StringDomainResult result = BuildResult([], samplingCoverage: 0.01);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        ConfidenceBandBlock band = ConfidenceBand(section);
        band.Score.Should().Be(0.50);
        band.Band.Should().Be("Medium");
        band.Caveats.Should().Contain(c => c.Contains("Sampling coverage is below 5%"));
    }

    [Fact]
    public void Build_FullConfidence_WhenSamplingCoverageIsZero_AndNoStringsScanned()
    {
        // SamplingCoverage == 0 with no strings on the heap is a "nothing to sample" case, not a
        // low-coverage warning — the < 0.05 penalty flag explicitly excludes 0 coverage.
        StringDomainResult result = BuildResult([], samplingCoverage: 0.0);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        ConfidenceBandBlock band = ConfidenceBand(section);
        band.Score.Should().Be(0.85);
        band.Caveats.Should().NotContain(c => c.Contains("Sampling coverage"));
    }

    [Fact]
    public void Build_RendersRetentionPathTable_WhenGcRootFound()
    {
        StringDomainResult result = BuildResult([], retentionPaths: [
            new DuplicateStringRetentionPath("OrderId=100001", 0x1000, HasGcRoot: true,
                RootPath: "Stack: MyApp.Cache@0x2000 -> System.String@0x1000", SearchTruncated: false),
        ]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        CompactTable? table = FindRetentionPathTable(section);
        table.Should().NotBeNull();
        object?[] row = table!.Rows[0].Values;
        row[1].Should().Be("0x1000");
        row[2].Should().Be("Stack: MyApp.Cache@0x2000 -> System.String@0x1000");
        row[3].Should().Be("No");
    }

    [Fact]
    public void Build_RendersNoRootFound_WhenSearchFailed()
    {
        StringDomainResult result = BuildResult([], retentionPaths: [
            new DuplicateStringRetentionPath("OrderId=100001", 0x1000, HasGcRoot: false,
                RootPath: null, SearchTruncated: true),
        ]);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        CompactTable? table = FindRetentionPathTable(section);
        table.Should().NotBeNull();
        object?[] row = table!.Rows[0].Values;
        row[2].Should().Be("(no root found)");
        row[3].Should().Be("Yes");
    }

    [Fact]
    public void Build_NoRetentionPathTable_WhenNoPathsComputed()
    {
        StringDomainResult result = BuildResult([], retentionPaths: null);

        AnalyzerDetailSection section = new StringSectionBuilder().Build(result);

        FindRetentionPathTable(section).Should().BeNull();
    }
}
