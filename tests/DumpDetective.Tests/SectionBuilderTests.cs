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

        // At least two tables (top by size, top by count); no histogram when null
        section.Blocks.OfType<TableBlock>().Should().HaveCountGreaterThanOrEqualTo(2);

        TableBlock sizeTable = section.Blocks.OfType<TableBlock>().First();
        sizeTable.Rows[0].Cells[0].Display.Should().Contain("System.String");
        // Avg Size column must be present (4 cells per row)
        sizeTable.Rows[0].Cells.Should().HaveCount(4);
    }

    [Fact]
    public void MemorySectionBuilder_Build_EmitsHistogramTableWhenBucketsPresent()
    {
        var histogram = new List<SizeBucketEntry>
        {
            new("< 16 B",    1_000_000, 12_000_000),
            new("16–63 B",   5_000_000, 200_000_000),
            new("64–255 B",  2_000_000, 320_000_000),
            new("256–1023 B",  500_000, 350_000_000),
            new("1 KB–16 KB",  100_000, 800_000_000),
            new("16 KB–85 KB",  10_000, 500_000_000),
            new("85 KB–1 MB",    1_000, 200_000_000),
            new("≥ 1 MB",          50, 100_000_000),
        };

        var domain = Stamped(new MemoryDomainResult(
            TotalBytes:        2_482_000_000,
            LohBytes:           300_000_000,
            LohPercent:         12.1,
            TotalObjects:       8_611_050,
            LohObjects:          11_050,
            LohThresholdBytes:    85_000,
            UniqueTypes:           4_500,
            TopTypesBySize:   [new TypeSnapshot("System.Byte[]", 11_050, 300_000_000, 300_000_000, AverageSize: 27_149)],
            TopTypesByCount:  [new TypeSnapshot("System.String", 5_000_000, 200_000_000, 0, AverageSize: 40)],
            SizeBucketHistogram: histogram),
            "Memory Analysis", "Memory");

        var builder = new MemorySectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        // Three tables: size-distribution histogram + top-by-size + top-by-count
        section.Blocks.OfType<TableBlock>().Should().HaveCount(3);

        TableBlock histTable = section.Blocks.OfType<TableBlock>().First();
        histTable.Headers.Should().Contain("Size Range");
        histTable.Rows.Should().HaveCount(8);
        histTable.Rows[0].Cells[0].Display.Should().Be("< 16 B");
        histTable.Rows[0].Cells[1].RawValue.Should().Be(1_000_000);

        // Avg Size cell must carry a RawValue when AverageSize > 0
        TableBlock sizeTable = section.Blocks.OfType<TableBlock>().Skip(1).First();
        sizeTable.Rows[0].Cells[3].RawValue.Should().Be(27_149);
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

    // ── AsyncTask ─────────────────────────────────────────────────────────────

    [Fact]
    public void AsyncTaskSectionBuilder_CanHandle_ReturnsTrueForAsyncTaskDomainResult()
    {
        var domain = Stamped(new AsyncTaskDomainResult(
            TotalTasks: 0, PendingTasks: 0, RunningTasks: 0, FaultedTasks: 0,
            CanceledTasks: 0, CompletedTasks: 0, OrphanedTasks: 0,
            MaxContinuationDepth: 0, AvgContinuationDepth: 0.0, TaskScanLimited: false,
            TopPendingTaskTypes: [], TopFaultedTaskTypes: [],
            TopContinuationTypes: [], TopOrphanedTasks: []),
            "Async Task Analysis", "Async");

        new AsyncTaskSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void AsyncTaskSectionBuilder_CanHandle_ReturnsFalseForUnrelatedResult()
    {
        var unrelated = Stamped(new MemoryDomainResult(
            TotalBytes: 0, LohBytes: 0, LohPercent: 0, TotalObjects: 0, LohObjects: 0,
            LohThresholdBytes: 0, UniqueTypes: 0, TopTypesBySize: [], TopTypesByCount: []),
            "Memory Analysis", "Memory");

        new AsyncTaskSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void AsyncTaskSectionBuilder_Build_EmitsSummaryMetricsTablesAndOrphans()
    {
        var orphaned = new List<OrphanedTaskSnapshot>
        {
            new(0xDEAD_BEEF_0001UL, "System.Threading.Tasks.Task`1[[System.String]]", "System.String", 120),
            new(0xDEAD_BEEF_0002UL, "System.Threading.Tasks.Task",                   null,            96),
        };

        var domain = Stamped(new AsyncTaskDomainResult(
            TotalTasks:           1_200,
            PendingTasks:         450,
            RunningTasks:         12,
            FaultedTasks:         33,
            CanceledTasks:        5,
            CompletedTasks:       700,
            OrphanedTasks:        orphaned.Count,
            MaxContinuationDepth: 8,
            AvgContinuationDepth: 3.4,
            TaskScanLimited:      false,
            TopPendingTaskTypes:  [new NameCountEntry("System.Threading.Tasks.Task`1[[Foo]]", 400)],
            TopFaultedTaskTypes:  [new NameCountEntry("System.Threading.Tasks.Task",           33)],
            TopContinuationTypes: [new NameCountEntry("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", 120)],
            TopOrphanedTasks:     orphaned),
            "Async Task Analysis", "Async");

        var builder = new AsyncTaskSectionBuilder();
        builder.CanHandle(domain).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(domain);

        section.AnalyzerName.Should().Be("Async Task Analysis");
        section.DisplayTitle.Should().Be("Async & Task Analysis");
        section.SortOrder.Should().Be(28);

        // Must have at least one heading block
        section.Blocks.OfType<HeadingBlock>().Should().NotBeEmpty();

        // Total tasks metric must be present with correct raw value
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Total Tasks" && m.RawValue == 1_200);

        // Max chain depth metric
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Max Chain Depth" && m.RawValue == 8);

        // Orphaned tasks table must contain both entries
        TableBlock? orphanTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("Orphaned", StringComparison.OrdinalIgnoreCase));
        orphanTable.Should().NotBeNull("orphaned tasks table must be emitted when orphans exist");
        orphanTable!.Rows.Should().HaveCount(2);
        orphanTable.Rows[0].Cells[1].Display.Should().Contain("Task`1");
    }
}
