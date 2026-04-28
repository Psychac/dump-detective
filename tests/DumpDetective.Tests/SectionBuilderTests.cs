using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests;

/// <summary>
/// H3 — unit tests for IAnalyzerSectionBuilder implementations.
/// Pattern: construct a known domain result → call builder.Build() → assert block structure.
/// No formatter, no text parsing involved.
/// </summary>
public sealed class SectionBuilderTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static T Stamped<T>(T domain, string analyzerName, string category)
        where T : AnalyzerDomainResult
        => domain with { AnalyzerName = analyzerName, Category = category };

    // ── Memory ────────────────────────────────────────────────────────────────

    [Fact]
    public void MemorySectionBuilder_Build_EmitsHeadingMetricsAndTables()
    {
        var domain = Stamped(new MemoryDomainResult(
            TotalBytes:       1_073_741_824,
            LohBytes:         268_435_456,
            LohPercent:       25.0,
            TotalObjects:     1_500_000,
            LohObjects:       8_000,
            LohThresholdBytes: 85_000,
            UniqueTypes:      3_200,
            TopTypesBySize:   [new TypeSnapshot("System.String",    500_000, 400_000_000, 0)],
            TopTypesByCount:  [new TypeSnapshot("System.Object[]",  200_000, 160_000_000, 0)]),
            "Memory Analysis", "Memory");

        var builder = new MemorySectionBuilder();

        builder.CanHandle(domain).Should().BeTrue();
        AnalyzerDetailSection section = builder.Build(domain);

        section.AnalyzerName.Should().Be("Memory Analysis");
        section.SortOrder.Should().Be(20);
        section.Blocks[0].Should().BeOfType<HeadingBlock>();

        // TotalBytes + TotalObjects metrics must be present with numeric RawValue
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.RawValue > 0, "total bytes metric must carry raw value");

        // At least two tables (top by size, top by count)
        section.Blocks.OfType<TableBlock>().Should().HaveCountGreaterThanOrEqualTo(2);

        TableBlock sizeTable = section.Blocks.OfType<TableBlock>().First();
        sizeTable.Rows[0].Cells[0].Display.Should().Contain("System.String");
    }

    [Fact]
    public void MemorySectionBuilder_CanHandle_ReturnsFalseForUnrelatedResult()
    {
        var builder  = new MemorySectionBuilder();
        var unrelated = Stamped(new CrashDomainResult(0, 0,
            new Dictionary<string, int>(), new Dictionary<string, int>()),
            "Crash Analysis", "Crash");

        builder.CanHandle(unrelated).Should().BeFalse();
    }

    // ── Crash ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CrashSectionBuilder_Build_EmitsMetricsAndExceptionTable()
    {
        var exCounts = new Dictionary<string, int>
        {
            ["System.NullReferenceException"]      = 3,
            ["System.InvalidOperationException"]   = 1
        };
        var domain = Stamped(new CrashDomainResult(
            TotalExceptions:          4,
            ActiveExceptions:         2,
            ExceptionTypeCounts:      exCounts,
            ActiveExceptionTypeCounts: new Dictionary<string, int>
                { ["System.NullReferenceException"] = 2 }),
            "Crash Analysis", "Crash");

        var builder = new CrashSectionBuilder();

        builder.CanHandle(domain).Should().BeTrue();
        AnalyzerDetailSection section = builder.Build(domain);

        section.SortOrder.Should().Be(10);
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Value.Contains("4") || m.Value.Contains("2"));

        // Exception type count table
        TableBlock? table = section.Blocks.OfType<TableBlock>().FirstOrDefault();
        table.Should().NotBeNull("should emit an exception type table");
        table!.Rows.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ── GC Handle ─────────────────────────────────────────────────────────────

    [Fact]
    public void GCHandleSectionBuilder_Build_EmitsHandleKindTable()
    {
        var handlesByKind = new List<NameCountEntry>
        {
            new("Strong",    120),
            new("Pinned",    8),
            new("WeakShort", 45)
        };
        var domain = Stamped(new GCHandleDomainResult(
            TotalHandles:         173,
            StrongLikeHandles:    120,
            WeakLikeHandles:      45,
            PinnedHandleTargets:  8,
            HandlesByKind:        handlesByKind,
            TopTargetTypes:       [new NameCountEntry("byte[]", 8)]),
            "GC Handle Analysis", "GCHandle");

        var builder = new GCHandleSectionBuilder();

        builder.CanHandle(domain).Should().BeTrue();
        AnalyzerDetailSection section = builder.Build(domain);

        section.SortOrder.Should().Be(45);

        TableBlock? kindTable = section.Blocks.OfType<TableBlock>().FirstOrDefault();
        kindTable.Should().NotBeNull("should emit a handles-by-kind table");
        kindTable!.Rows.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    // ── Collection ────────────────────────────────────────────────────────────

    [Fact]
    public void CollectionSectionBuilder_Build_EmitsMetricsAndWastefulTable()
    {
        var wasteful = new List<WastefulCollectionSnapshot>
        {
            new("System.Collections.Generic.List<System.String>",
                Kind: CollectionKind.List, Count: 200, Capacity: 2000,
                FillRate: 0.10, WastedMemory: 16_000, Address: 0x1000)
        };
        var domain = Stamped(new CollectionDomainResult(
            TotalCollections:        500,
            Dictionaries:            10,
            Lists:                   200,
            ArrayLists:              0,
            Stacks:                  0,
            SortedLists:             0,
            SortedSets:              0,
            HashSets:                5,
            Queues:                  3,
            TotalWastedMemory:       16_000,
            WastefulCollectionCount: 1,
            TopWastefulCollections:  wasteful),
            "Collection Analysis", "Collection");

        var builder = new CollectionSectionBuilder();

        builder.CanHandle(domain).Should().BeTrue();
        AnalyzerDetailSection section = builder.Build(domain);

        section.SortOrder.Should().Be(50);
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Value == "500" || m.Value == "1");

        TableBlock? table = section.Blocks.OfType<TableBlock>().FirstOrDefault();
        table.Should().NotBeNull();
        table!.Rows.Should().HaveCount(1);
        table.Rows[0].Cells[0].Display.Should().Contain("List");
    }

    // ── LOH Fragmentation ─────────────────────────────────────────────────────

    [Fact]
    public void LohFragmentationSectionBuilder_Build_EmitsSegmentTable()
    {
        var segments = new List<LohSegmentSnapshot>
        {
            new(Address: 0x1000000000UL, FragmentationPercent: 18.5,
                FreeBytes: 24_000_000, LargestFreeBlock: 5_000_000)
        };
        var domain = Stamped(new LohFragmentationDomainResult(
            SegmentCount:         1,
            TotalBytes:           134_217_728,
            FreeBytes:            24_000_000,
            UsedBytes:            110_217_728,
            FreeBlockCount:       42,
            FragmentationPercent: 18.5,
            LargestFreeBlock:     5_000_000,
            TopFragmentedSegments: segments),
            "LOH Fragmentation Analysis", "LOH");

        var builder = new LohFragmentationSectionBuilder();

        builder.CanHandle(domain).Should().BeTrue();
        AnalyzerDetailSection section = builder.Build(domain);

        section.SortOrder.Should().Be(55);
        section.Blocks.OfType<MetricBlock>().Should().NotBeEmpty();

        TableBlock? segTable = section.Blocks.OfType<TableBlock>().FirstOrDefault();
        segTable.Should().NotBeNull("should emit a per-segment table");
        segTable!.Rows.Should().HaveCount(1);
        segTable.Rows[0].Cells[0].Display.Should().StartWith("0x");
    }
}
