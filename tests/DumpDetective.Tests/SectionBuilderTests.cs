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
            TotalBytes: 1_073_741_824,
            LohBytes: 268_435_456,
            LohPercent: 25.0,
            TotalObjects: 1_500_000,
            LohObjects: 8_000,
            LohThresholdBytes: 85_000,
            UniqueTypes: 3_200,
            TopTypesBySize: [new TypeSnapshot("System.String", 500_000, 400_000_000, 0)],
            TopTypesByCount: [new TypeSnapshot("System.Object[]", 200_000, 160_000_000, 0)]),
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
            TotalBytes: 2_482_000_000,
            LohBytes: 300_000_000,
            LohPercent: 12.1,
            TotalObjects: 8_611_050,
            LohObjects: 11_050,
            LohThresholdBytes: 85_000,
            UniqueTypes: 4_500,
            TopTypesBySize: [new TypeSnapshot("System.Byte[]", 11_050, 300_000_000, 300_000_000, AverageSize: 27_149)],
            TopTypesByCount: [new TypeSnapshot("System.String", 5_000_000, 200_000_000, 0, AverageSize: 40)],
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
        var builder = new MemorySectionBuilder();
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
            ["System.NullReferenceException"] = 3,
            ["System.InvalidOperationException"] = 1
        };
        var domain = Stamped(new CrashDomainResult(
            TotalExceptions: 4,
            ActiveExceptions: 2,
            ExceptionTypeCounts: exCounts,
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
            TotalHandles: 173,
            StrongLikeHandles: 120,
            WeakLikeHandles: 45,
            PinnedHandleTargets: 8,
            HandlesByKind: handlesByKind,
            TopTargetTypes: [new NameCountEntry("byte[]", 8)]),
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
            TotalCollections: 500,
            Dictionaries: 10,
            Lists: 200,
            ArrayLists: 0,
            Stacks: 0,
            SortedLists: 0,
            SortedSets: 0,
            HashSets: 5,
            Queues: 3,
            TotalWastedMemory: 16_000,
            WastefulCollectionCount: 1,
            TopWastefulCollections: wasteful),
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
            SegmentCount: 1,
            TotalBytes: 134_217_728,
            FreeBytes: 24_000_000,
            UsedBytes: 110_217_728,
            FreeBlockCount: 42,
            FragmentationPercent: 18.5,
            LargestFreeBlock: 5_000_000,
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
            TotalTasks: 1_200,
            PendingTasks: 450,
            RunningTasks: 12,
            FaultedTasks: 33,
            CanceledTasks: 5,
            CompletedTasks: 700,
            OrphanedTasks: orphaned.Count,
            MaxContinuationDepth: 8,
            AvgContinuationDepth: 3.4,
            TaskScanLimited: false,
            TopPendingTaskTypes: [new NameCountEntry("System.Threading.Tasks.Task`1[[Foo]]", 400)],
            TopFaultedTaskTypes: [new NameCountEntry("System.Threading.Tasks.Task", 33)],
            TopContinuationTypes: [new NameCountEntry("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", 120)],
            TopOrphanedTasks: orphaned),
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

        section.Blocks.OfType<TableBlock>()
            .SelectMany(t => t.Rows)
            .Should().Contain(row => row.Cells[0].Display == "RanToCompletion");

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

    [Fact]
    public void AsyncAnalysisSectionBuilder_Build_EmitsTaskSummaryAndThreadPoolContext()
    {
        var asyncDomain = Stamped(new AsyncTaskDomainResult(
            TotalTasks: 1_200,
            PendingTasks: 450,
            RunningTasks: 12,
            FaultedTasks: 33,
            CanceledTasks: 5,
            CompletedTasks: 700,
            OrphanedTasks: 2,
            TotalTaskContinuations: 1_500,
            MaxContinuationDepth: 8,
            AvgContinuationDepth: 3.4,
            TaskScanLimited: false,
            TopPendingTaskTypes: [new NameCountEntry("System.Threading.Tasks.Task`1[[Foo]]", 400)],
            TopFaultedTaskTypes: [new NameCountEntry("System.Threading.Tasks.Task", 33)],
            TopContinuationTypes: [new NameCountEntry("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", 120)],
            TopOrphanedTasks: [new OrphanedTaskSnapshot(0xDEAD_BEEF_0001UL, "System.Threading.Tasks.Task`1[[System.String]]", "System.String", 120)],
            TopDeepestChains: [new ContinuationChainSnapshot(0xDEAD_BEEF_0001UL, "System.Threading.Tasks.Task`1[[System.String]]", 8, ["System.Threading.Tasks.Task", "System.Runtime.CompilerServices.AsyncTaskMethodBuilder"]) ]),
            "Async Task Analysis", "Async");

        var hangDomain = Stamped(new HangDomainResult(
            TotalAliveThreads: 24,
            WaitingThreadCount: 4,
            ThreadsHoldingLocks: 2,
            WaitingPercent: 16.7,
            WaitCategoryBreakdown: new Dictionary<string, int> { ["Task"] = 4 },
            TotalTaskContinuations: 1_500,
            QueuedWorkItems: 42,
            TotalTasks: 1_200,
            PendingTasks: 450,
            FaultedTasks: 33,
            CanceledTasks: 5,
            RuntimeThreadPoolDataAvailable: true,
            RuntimeMinThreads: 8,
            RuntimeMaxThreads: 32,
            RuntimeActiveWorkerThreads: 8,
            RuntimeIdleWorkerThreads: 24,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeCpuUtilization: 15,
            IsStarved: false,
            TaskScanLimited: false,
            HealthScore: 92),
            "Hang Analysis", "Hang");

        var runs = new[]
        {
            new AnalyzerRunResult("Async Task Analysis", AnalyzerExecutionStatus.Success, TimeSpan.FromMilliseconds(5), asyncDomain, null, null),
            new AnalyzerRunResult("Hang Analysis", AnalyzerExecutionStatus.Success, TimeSpan.FromMilliseconds(5), hangDomain, null, null),
        };

        var resultSet = new AnalyzerResultSet(runs);
        var builder = new AsyncAnalysisSectionBuilder();

        builder.CanBuild(resultSet).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(resultSet);

        section.SortOrder.Should().Be(1350);
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Total tasks" && m.RawValue == 1_200);

        section.Blocks.OfType<TableBlock>()
            .SelectMany(t => t.Rows)
            .Should().Contain(row => row.Cells[0].Display == "RanToCompletion");

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Queued work items" && m.RawValue == 42);

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Runtime TP data" && m.Value == "Available");
    }

    // ── Lock Graph ────────────────────────────────────────────────────────────

    [Fact]
    public void LockGraphSectionBuilder_CanHandle_ReturnsTrue()
    {
        var domain = Stamped(new LockGraphDomainResult(0, 0, 0, 0), "Lock Graph Analysis", "Locks");
        new LockGraphSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void LockGraphSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(new CrashDomainResult(0, 0,
            new Dictionary<string, int>(), new Dictionary<string, int>()),
            "Crash Analysis", "Crash");
        new LockGraphSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void LockGraphSectionBuilder_Build_EmitsSummaryMetricsAndNoDetailTablesWhenEmpty()
    {
        var domain = Stamped(new LockGraphDomainResult(
            TotalHeldLocks: 5,
            ContestedLockCount: 0,
            MaxWaitersOnSingleLock: 0,
            DeadlockCandidateCount: 0),
            "Lock Graph Analysis", "Locks");

        var builder = new LockGraphSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        section.AnalyzerName.Should().Be("Lock Graph Analysis");
        section.SortOrder.Should().Be(70);

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Held Locks" && m.RawValue == 5);

        // No contested or deadlock detail tables when empty
        section.Blocks.OfType<TableBlock>().Should().BeEmpty();
    }

    [Fact]
    public void LockGraphSectionBuilder_Build_EmitsContestedLockTableAndDeadlockCandidateTable()
    {
        var contestedDetails = new List<ContestedLockSnapshot>
        {
            new(0xABCD_1234UL, "System.Collections.Generic.Dictionary`2", 3, 42u, 1),
            new(0xABCD_5678UL, "System.Object", 1, null, 0),
        };
        var deadlockDetails = new List<DeadlockCandidateSnapshot>
        {
            new(42u, 9800u, ["System.Object"], "Thread 42 (OS: 9800) holds 1 lock(s), blocked at: Monitor.Enter"),
            new(43u, 9801u, ["System.Collections.Generic.Dictionary`2"], "Thread 43 (OS: 9801) holds 1 lock(s), blocked at: Monitor.Enter"),
        };
        var topTypes = new List<NameCountEntry>
        {
            new("System.Object", 4),
        };

        var domain = Stamped(new LockGraphDomainResult(
            TotalHeldLocks: 4,
            ContestedLockCount: 2,
            MaxWaitersOnSingleLock: 3,
            DeadlockCandidateCount: 2,
            TopContestedLockTypes: topTypes,
            DeadlockCandidateDetails: deadlockDetails,
            ContestedLockDetails: contestedDetails),
            "Lock Graph Analysis", "Locks");

        var builder = new LockGraphSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        // Summary metrics present
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Contested Locks" && m.RawValue == 2);
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Deadlock Candidates" && m.RawValue == 2);

        // Contested lock objects table
        TableBlock? contestedTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("Contested lock objects", StringComparison.OrdinalIgnoreCase));
        contestedTable.Should().NotBeNull("contested lock details table must be emitted");
        contestedTable!.Rows.Should().HaveCount(2);
        contestedTable.Rows[0].Cells[0].Display.Should().Contain("Dictionary");

        // Deadlock candidate threads table
        TableBlock? deadlockTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("Deadlock candidate threads", StringComparison.OrdinalIgnoreCase));
        deadlockTable.Should().NotBeNull("deadlock candidate table must be emitted");
        deadlockTable!.Rows.Should().HaveCount(2);
        deadlockTable.Rows[0].Cells[0].Display.Should().Be("42");
        deadlockTable.Rows[1].Cells[1].Display.Should().Be("9801");
    }

    // ── Allocation Pattern ────────────────────────────────────────────────────

    [Fact]
    public void AllocationPatternSectionBuilder_CanHandle_ReturnsTrue()
    {
        var domain = Stamped(new AllocationPatternDomainResult(
            Gen0CountPct: 0, Gen1CountPct: 0, Gen2CountPct: 0, LohCountPct: 0,
            Gen0SizePct: 0, Gen1SizePct: 0, Gen2SizePct: 0, LohSizePct: 0,
            Profile: AllocationProfile.Mixed,
            GCPressure: GCPressureLevel.Low,
            PromotionPressureScore: 0,
            TopTransientTypes: [],
            TopShortishTypes: [],
            TopLongLivedTypes: []),
            "Allocation Pattern Analysis", "GC");
        new AllocationPatternSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void AllocationPatternSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(new CrashDomainResult(0, 0,
            new Dictionary<string, int>(), new Dictionary<string, int>()),
            "Crash Analysis", "Crash");
        new AllocationPatternSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void AllocationPatternSectionBuilder_Build_EmitsSummaryMetricsAndPressureSignal()
    {
        var domain = Stamped(new AllocationPatternDomainResult(
            Gen0CountPct: 75.0, Gen1CountPct: 12.0, Gen2CountPct: 10.0, LohCountPct: 0.1,
            Gen0SizePct: 45.0, Gen1SizePct: 18.0, Gen2SizePct: 25.0, LohSizePct: 2.0,
            Profile: AllocationProfile.Transient,
            GCPressure: GCPressureLevel.Low,
            PromotionPressureScore: 8.4,
            TopTransientTypes: [],
            TopShortishTypes: [],
            TopLongLivedTypes: []),
            "Allocation Pattern Analysis", "GC");

        var builder = new AllocationPatternSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        section.AnalyzerName.Should().Be("Allocation Pattern Analysis");
        section.SortOrder.Should().Be(32);
        section.Blocks[0].Should().BeOfType<HeadingBlock>();

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Allocation Profile" && m.Value == "Transient");
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "GC Pressure Level" && m.Value == "Low");

        // Generation distribution table must always be present (4 rows: Gen0/1/2/LOH)
        TableBlock? genTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("distribution", StringComparison.OrdinalIgnoreCase));
        genTable.Should().NotBeNull("generation distribution table must always be emitted");
        genTable!.Rows.Should().HaveCount(4);
        genTable.Rows[0].Cells[0].Display.Should().Be("Gen0");
        genTable.Rows[3].Cells[0].Display.Should().Be("LOH");

        // No type tables when both lists are empty
        section.Blocks.OfType<TableBlock>()
            .Where(t => t != genTable)
            .Should().BeEmpty();
    }

    [Fact]
    public void AllocationPatternSectionBuilder_Build_EmitsTypeTablesWhenPopulated()
    {
        var shortLived = new List<TypeAllocationProfile>
        {
            new("System.String",                      5000, 200, 50, 0.05, AllocationProfile.Transient),
            new("System.Byte[]",                      3000, 100, 20, 0.04, AllocationProfile.Transient),
        };
        var longLived = new List<TypeAllocationProfile>
        {
            new("System.Collections.Generic.List`1",  100,  50, 900, 0.88, AllocationProfile.Retained),
        };

        var domain = Stamped(new AllocationPatternDomainResult(
            Gen0CountPct: 30.0, Gen1CountPct: 8.0, Gen2CountPct: 55.0, LohCountPct: 0.1,
            Gen0SizePct: 25.0, Gen1SizePct: 7.0, Gen2SizePct: 50.0, LohSizePct: 5.0,
            Profile: AllocationProfile.Retained,
            GCPressure: GCPressureLevel.High,
            PromotionPressureScore: 42.0,
            TopTransientTypes: shortLived,
            TopShortishTypes: [],
            TopLongLivedTypes: longLived),
            "Allocation Pattern Analysis", "GC");

        var builder = new AllocationPatternSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        // GC pressure level metric
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "GC Pressure Level" && m.Value == "High");

        // Short/transient tables — pick any non-distribution table that contains the short-lived types
        var otherTables = section.Blocks.OfType<TableBlock>()
            .Where(t => t.Caption == null || !t.Caption.Contains("distribution", StringComparison.OrdinalIgnoreCase))
            .ToList();

        otherTables.Should().NotBeEmpty("type tables must be emitted when populated");
        otherTables.Any(t => t.Rows.Count == 2 && t.Rows[0].Cells[0].Display.Contains("String")).Should().BeTrue("short-lived types must appear in one of the type tables");

        // Long-lived table
        TableBlock? longTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
        longTable.Should().NotBeNull("long-lived type table must be emitted");
        longTable!.Rows.Should().HaveCount(1);
        longTable.Rows[0].Cells[0].Display.Should().Contain("List");
    }

    // ── ObjectShapeAnalyzer ───────────────────────────────────────────────────

    [Fact]
    public void ObjectShapeSectionBuilder_CanHandle_ReturnsTrue()
    {
        var domain = Stamped(new ObjectShapeAnalyzerDomainResult(
            TopReferenceHeavyTypes: [],
            TopValueHeavyTypes: [],
            TotalTypesAnalyzed: 0,
            AvgRefFieldsPerType: 0),
            "Object Shape Analysis", "Memory");
        new ObjectShapeSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void ObjectShapeSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(new CrashDomainResult(0, 0,
            new Dictionary<string, int>(), new Dictionary<string, int>()),
            "Crash Analysis", "Crash");
        new ObjectShapeSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void ObjectShapeSectionBuilder_Build_EmitsSummaryMetricsWhenEmpty()
    {
        var domain = Stamped(new ObjectShapeAnalyzerDomainResult(
            TopReferenceHeavyTypes: [],
            TopValueHeavyTypes: [],
            TotalTypesAnalyzed: 42,
            AvgRefFieldsPerType: 3.7),
            "Object Shape Analysis", "Memory");

        var builder = new ObjectShapeSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        section.AnalyzerName.Should().Be("Object Shape Analysis");
        section.SortOrder.Should().Be(33);
        section.Blocks[0].Should().BeOfType<HeadingBlock>();

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Types Analyzed" && m.RawValue == 42.0);
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Avg Ref Fields / Type" && m.RawValue == 3.7);

        // No type tables when both lists are empty
        section.Blocks.OfType<TableBlock>().Should().BeEmpty();
    }

    [Fact]
    public void ObjectShapeSectionBuilder_Build_EmitsTypeTablesWhenPopulated()
    {
        var refHeavy = new List<TypeShapeProfile>
        {
            new("System.Collections.Generic.Dictionary`2",
                TotalFields: 8, ReferenceFields: 6, ValueFields: 2,
                ReferenceFieldRatio: 0.75, InstanceCount: 5000,
                IsFinalizable: false, IsValueType: false,
                BaseTypeChainDepth: 2, InterfaceCount: 3,
                Category: ObjectShapeCategory.ReferenceHeavy),
        };
        var valHeavy = new List<TypeShapeProfile>
        {
            new("System.Drawing.Rectangle",
                TotalFields: 4, ReferenceFields: 0, ValueFields: 4,
                ReferenceFieldRatio: 0.0, InstanceCount: 20000,
                IsFinalizable: false, IsValueType: true,
                BaseTypeChainDepth: 1, InterfaceCount: 2,
                Category: ObjectShapeCategory.ValueHeavy),
        };

        var domain = Stamped(new ObjectShapeAnalyzerDomainResult(
            TopReferenceHeavyTypes: refHeavy,
            TopValueHeavyTypes: valHeavy,
            TotalTypesAnalyzed: 150,
            AvgRefFieldsPerType: 2.4),
            "Object Shape Analysis", "Memory");

        var builder = new ObjectShapeSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        // Reference-heavy table
        TableBlock? refTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("reference-heavy", StringComparison.OrdinalIgnoreCase));
        refTable.Should().NotBeNull("reference-heavy type table must be emitted");
        refTable!.Rows.Should().HaveCount(1);
        refTable.Rows[0].Cells[0].Display.Should().Contain("Dictionary");

        // Value-heavy table
        TableBlock? valTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("value-heavy", StringComparison.OrdinalIgnoreCase));
        valTable.Should().NotBeNull("value-heavy type table must be emitted");
        valTable!.Rows.Should().HaveCount(1);
        valTable.Rows[0].Cells[0].Display.Should().Contain("Rectangle");
    }

    // ── GCRootSectionBuilder tests ────────────────────────────────────────────

    [Fact]
    public void GCRootSectionBuilder_CanHandle_ReturnsTrue()
    {
        var domain = Stamped(new GCRootDomainResult(
            TotalRoots: 0, ByKind: [], TopRootsBySeverity: [],
            RootPaths: [], PathSearchCapped: false, PathSearchCappedCount: 0),
            "GC Root Analysis", "Memory");

        new GCRootSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void GCRootSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(new GCRootDomainResult(
            TotalRoots: 0, ByKind: [], TopRootsBySeverity: [],
            RootPaths: [], PathSearchCapped: false, PathSearchCappedCount: 0),
            "Other", "Other");
        // Use a clearly unrelated result type
        new GCRootSectionBuilder().CanHandle(
            Stamped(new ObjectShapeAnalyzerDomainResult([], [], 0, 0), "Other", "Other"))
            .Should().BeFalse();
    }

    [Fact]
    public void GCRootSectionBuilder_Build_EmitsSummaryMetricsWhenEmpty()
    {
        var domain = Stamped(new GCRootDomainResult(
            TotalRoots: 0, ByKind: [], TopRootsBySeverity: [],
            RootPaths: [], PathSearchCapped: false, PathSearchCappedCount: 0),
            "GC Root Analysis", "Memory");

        var builder = new GCRootSectionBuilder();
        builder.CanHandle(domain).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(domain);
        section.AnalyzerName.Should().Be("GC Root Analysis");
        section.DisplayTitle.Should().Be("GC Root Intelligence");
        section.SortOrder.Should().Be(24);

        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Total GC Roots" && m.RawValue == 0);
    }

    [Fact]
    public void GCRootSectionBuilder_Build_EmitsKindAndFindingTablesWhenPopulated()
    {
        var byKind = new List<RootKindSummary>
        {
            new("StrongHandle",    120, 50_000_000, 25.0),
            new("Stack",           800, 10_000_000, 5.0),
            new("FinalizerQueue",   40,  1_000_000, 0.5),
        };
        var topFindings = new List<RootFinding>
        {
            new("StrongHandle", 0xABCD_0000_0001UL, null, "System.Collections.Generic.List`1", 0xDEAD_0001UL, 50_000_000, 300),
            new("Stack",        0xABCD_0000_0002UL, null, "MyApp.SomeService",                 0xDEAD_0002UL,  5_000_000, 100),
        };
        var rootPaths = new List<RootPathFinding>
        {
            new(0xDEAD_0001UL, "System.Collections.Generic.List`1", "StrongHandle",
                ["System.Object[]", "MyApp.SomeService"], 2, false),
        };

        var domain = Stamped(new GCRootDomainResult(
            TotalRoots: 960,
            ByKind: byKind,
            TopRootsBySeverity: topFindings,
            RootPaths: rootPaths,
            PathSearchCapped: false,
            PathSearchCappedCount: 0),
            "GC Root Analysis", "Memory");

        var builder = new GCRootSectionBuilder();
        AnalyzerDetailSection section = builder.Build(domain);

        // Summary metric
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Total GC Roots" && m.RawValue == 960);

        // Kind distribution table
        TableBlock? kindTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("kind", StringComparison.OrdinalIgnoreCase));
        kindTable.Should().NotBeNull("kind distribution table must be emitted");
        kindTable!.Rows.Should().HaveCount(3);

        // Top findings table
        TableBlock? findingTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("severity", StringComparison.OrdinalIgnoreCase));
        findingTable.Should().NotBeNull("findings table must be emitted");
        findingTable!.Rows.Should().HaveCount(2);
        findingTable.Rows[0].Cells[0].Display.Should().Be("StrongHandle");

        // Root paths table
        TableBlock? pathTable = section.Blocks.OfType<TableBlock>()
            .FirstOrDefault(t => t.Caption != null && t.Caption.Contains("path", StringComparison.OrdinalIgnoreCase));
        pathTable.Should().NotBeNull("paths table must be emitted");
        pathTable!.Rows.Should().HaveCount(1);
    }

    // ── EventLeakSectionBuilder tests ─────────────────────────────────────────

    [Fact]
    public void EventLeakSectionBuilder_CanHandle_ReturnsTrue()
    {
        var domain = Stamped(new EventLeakDomainResult(0, 0, 0, 0), "Event Leak Analysis", "Events");
        new EventLeakSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void EventLeakSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(new GCRootDomainResult(
            TotalRoots: 0, ByKind: [], TopRootsBySeverity: [],
            RootPaths: [], PathSearchCapped: false, PathSearchCappedCount: 0),
            "Other", "Other");
        new EventLeakSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void EventLeakSectionBuilder_Build_EmitsSummaryMetricsWhenEmpty()
    {
        var domain = Stamped(
            new EventLeakDomainResult(0, 0, 0, 0,
                TotalEventsScanned: 42, TotalPublisherInstances: 10),
            "Event Leak Analysis", "Events");

        var builder = new EventLeakSectionBuilder();
        builder.CanHandle(domain).Should().BeTrue();

        AnalyzerDetailSection section = builder.Build(domain);
        section.AnalyzerName.Should().Be("Event Leak Analysis");
        section.DisplayTitle.Should().Be("Event & Delegate Analysis");
        section.SortOrder.Should().Be(80);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Potential Leak Groups" && m.RawValue == 0);
        metrics.Should().Contain(m => m.Label == "Events Scanned" && m.RawValue == 42);
        metrics.Should().Contain(m => m.Label == "Publisher Instances" && m.RawValue == 10);
    }

    [Fact]
    public void EventLeakSectionBuilder_Build_EmitsGroupDetailWithRetainedBytes()
    {
        var group = new EventLeakGroupSnapshot(
            PublisherType: "MyApp.Service",
            EventFieldName: "OnDataReceived",
            IsStatic: false,
            SeverityScore: 25,
            InstanceCount: 3,
            TotalSubscribers: 9,
            AverageSubscribers: 3.0,
            MinSubscribers: 2,
            MaxSubscribers: 4,
            TopSubscriberTypes: [new NameCountEntry("MyApp.Handler", 9)],
            EstimatedSubscriberRetainedBytes: 4096);

        var domain = Stamped(
            new EventLeakDomainResult(
                TotalEventLeakInstances: 1,
                TotalSubscribers: 9,
                StaticEventLeakCount: 0,
                InstanceEventLeakCount: 1,
                TopLeakGroups: [group]),
            "Event Leak Analysis", "Events");

        AnalyzerDetailSection section = new EventLeakSectionBuilder().Build(domain);

        // Group details with retained bytes metric
        section.Blocks.OfType<MetricBlock>()
            .Should().Contain(m => m.Label == "Est. Retained Bytes" && m.RawValue > 0);

        // Subscriber type table inside group
        section.Blocks.OfType<TableBlock>().Should().NotBeEmpty();
    }

    // ── FinalizableObjectSectionBuilder tests ─────────────────────────────────

    [Fact]
    public void FinalizableObjectSectionBuilder_CanHandle_ReturnsTrueForFinalizableObjectDomainResult()
    {
        var domain = Stamped(
            new FinalizableObjectDomainResult(
                TotalFinalizableObjects: 0, TotalFinalizableBytes: 0,
                Gen0Count: 0, Gen1Count: 0, Gen2Count: 0,
                FinalizerQueueCount: 0, FinalizerQueueRetainedBytes: 0,
                PotentialResurrectionDetected: false,
                TopFinalizableTypesByGen2Count: [],
                TopQueueEntriesByRetainedSize: []),
            "Finalizable Object Analysis", "Memory");
        new FinalizableObjectSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void FinalizableObjectSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new EventLeakDomainResult(0, 0, 0, 0),
            "Event Leak Analysis", "Events");
        new FinalizableObjectSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void FinalizableObjectSectionBuilder_Build_EmitsSummaryMetricsAndTables()
    {
        var queueEntry = new FinalizerQueueEntry(
            Address: 0x1234_5678,
            TypeName: "MyApp.Resource",
            ShallowSize: 128,
            EstimatedRetainedBytes: 4096,
            IsDisposableType: true,
            DisposedFieldFound: true,
            DisposedFieldValue: false);

        var typeProfile = new TypeGenerationProfile(
            TypeName: "MyApp.Resource",
            Gen0Count: 10,
            Gen1Count: 5,
            Gen2Count: 1500,
            LohCount: 0);

        var domain = Stamped(
            new FinalizableObjectDomainResult(
                TotalFinalizableObjects: 2000,
                TotalFinalizableBytes: 256_000,
                Gen0Count: 10,
                Gen1Count: 5,
                Gen2Count: 1500,
                FinalizerQueueCount: 3,
                FinalizerQueueRetainedBytes: 4096,
                PotentialResurrectionDetected: true,
                TopFinalizableTypesByGen2Count: [typeProfile],
                TopQueueEntriesByRetainedSize: [queueEntry]),
            "Finalizable Object Analysis", "Memory");

        AnalyzerDetailSection section = new FinalizableObjectSectionBuilder().Build(domain);

        section.AnalyzerName.Should().Be("Finalizable Object Analysis");
        section.SortOrder.Should().Be(46);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total Finalizable Objects" && m.RawValue == 2000);
        metrics.Should().Contain(m => m.Label == "Finalizer Queue Objects" && m.RawValue == 3);
        metrics.Should().Contain(m => m.Label.Contains("Resurrection"));

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(2, "should emit type table and queue table");

        var typeTable = tables.FirstOrDefault(t => t.Caption!.Contains("Gen2"));
        typeTable.Should().NotBeNull("type-by-gen2 table must be emitted");
        typeTable!.Rows.Should().HaveCount(1);

        var queueTable = tables.FirstOrDefault(t => t.Caption!.Contains("queue"));
        queueTable.Should().NotBeNull("queue entries table must be emitted");
        queueTable!.Rows.Should().HaveCount(1);
    }

    // ── AsyncStateMachineSectionBuilder tests ─────────────────────────────────

    [Fact]
    public void AsyncStateMachineSectionBuilder_CanHandle_ReturnsTrueForAsyncStateMachineDomainResult()
    {
        var domain = Stamped(
            new AsyncStateMachineDomainResult(
                TotalStateMachines: 0,
                TotalStateMachineBytes: 0,
                TopStateMachineTypes: [],
                TopByCapturedSize: [],
                SuspendedMethodMap: [],
                ScanLimited: false),
            "Async State Machine Analysis", "Memory");
        new AsyncStateMachineSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void AsyncStateMachineSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new EventLeakDomainResult(0, 0, 0, 0),
            "Event Leak Analysis", "Events");
        new AsyncStateMachineSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void AsyncStateMachineSectionBuilder_Build_EmitsSummaryMetricsAndTables()
    {
        var typeProfile = new StateMachineTypeProfile(
            TypeName: "MyApp.Service+<ProcessAsync>d__3",
            OriginatingMethod: "ProcessAsync",
            DeclaringType: "MyApp.Service",
            Count: 250,
            TotalBytes: 500_000,
            AvgStateValue: -1,
            ReferenceFieldCount: 4);

        var highCapture = new HighCaptureStateMachine(
            Address: 0xABCD_1234,
            TypeName: "MyApp.Service+<ProcessAsync>d__3",
            TotalCapturedRefBytes: 2_097_152,
            LargeCaptures: ["_dbContext (MyApp.Data.AppDbContext, 1.8 MB)"]);

        var suspendedEntry = new SuspendedMethodEntry(
            DeclaringType: "MyApp.Service",
            MethodName: "ProcessAsync",
            SuspendedCount: 250,
            TotalBytes: 500_000);

        var domain = Stamped(
            new AsyncStateMachineDomainResult(
                TotalStateMachines: 250,
                TotalStateMachineBytes: 500_000,
                TopStateMachineTypes: [typeProfile],
                TopByCapturedSize: [highCapture],
                SuspendedMethodMap: [suspendedEntry],
                ScanLimited: false),
            "Async State Machine Analysis", "Memory");

        AnalyzerDetailSection section = new AsyncStateMachineSectionBuilder().Build(domain);

        section.AnalyzerName.Should().Be("Async State Machine Analysis");
        section.SortOrder.Should().Be(48);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total State Machines" && m.RawValue == 250);
        metrics.Should().Contain(m => m.Label == "Distinct Types");
        metrics.Should().Contain(m => m.Label == "Suspended Methods");

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(3, "should emit types, captures, and suspended method tables");

        var typeTable = tables.FirstOrDefault(t => t.Caption!.Contains("state machine types"));
        typeTable.Should().NotBeNull("top state machine types table must be emitted");
        typeTable!.Rows.Should().HaveCount(1);

        var captureTable = tables.FirstOrDefault(t => t.Caption!.Contains("captured"));
        captureTable.Should().NotBeNull("high-capture instances table must be emitted");
        captureTable!.Rows.Should().HaveCount(1);

        var suspendedTable = tables.FirstOrDefault(t => t.Caption!.Contains("Suspended"));
        suspendedTable.Should().NotBeNull("suspended method map table must be emitted");
        suspendedTable!.Rows.Should().HaveCount(1);
    }

    // ── ArraySectionBuilder tests ──────────────────────────────────────────────

    [Fact]
    public void ArraySectionBuilder_CanHandle_ReturnsTrueForArrayDomainResult()
    {
        var domain = Stamped(
            new ArrayDomainResult(
                TotalArrayObjects: 0, TotalArrayBytes: 0,
                MultiDimArrayCount: 0, LohArrayCount: 0, LohArrayBytes: 0,
                TopArrayTypesBySize: [], TopLargeArrays: [], TopSparseArrays: [],
                ScanLimited: false),
            "Array Analysis", "Memory");
        new ArraySectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void ArraySectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new EventLeakDomainResult(0, 0, 0, 0),
            "Event Leak Analysis", "Events");
        new ArraySectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void ArraySectionBuilder_Build_EmitsSummaryMetricsAndTables()
    {
        var typeProfile = new ArrayTypeProfile(
            ElementTypeName: "System.Byte",
            Rank: 1,
            Count: 5_000,
            TotalBytes: 104_857_600,
            IsMultiDimensional: false);

        var largeEntry = new LargeArrayEntry(
            Address: 0x1234_5678,
            ElementTypeName: "System.Byte",
            Length: 1_048_576,
            Rank: 1,
            Size: 1_048_600);

        var sparseEntry = new SparseArrayEntry(
            Address: 0x9ABC_DEF0,
            ElementTypeName: "System.Object",
            Length: 50_000,
            NullOrZeroCount: 40_000,
            SparseRatio: 0.80,
            WastedBytes: 25_000_000);

        var domain = Stamped(
            new ArrayDomainResult(
                TotalArrayObjects: 5_000,
                TotalArrayBytes: 104_857_600,
                MultiDimArrayCount: 12,
                LohArrayCount: 3,
                LohArrayBytes: 3_000_000,
                TopArrayTypesBySize: [typeProfile],
                TopLargeArrays: [largeEntry],
                TopSparseArrays: [sparseEntry],
                ScanLimited: false),
            "Array Analysis", "Memory");

        AnalyzerDetailSection section = new ArraySectionBuilder().Build(domain);

        section.AnalyzerName.Should().Be("Array Analysis");
        section.SortOrder.Should().Be(47);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total Array Objects" && m.RawValue == 5_000);
        metrics.Should().Contain(m => m.Label == "LOH Arrays");
        metrics.Should().Contain(m => m.Label == "Multi-Dimensional Arrays");

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(3, "should emit types, large arrays, and sparse arrays tables");

        var typeTable = tables.FirstOrDefault(t => t.Caption!.Contains("array types by total bytes"));
        typeTable.Should().NotBeNull("top array types table must be emitted");
        typeTable!.Rows.Should().HaveCount(1);

        var largeTable = tables.FirstOrDefault(t => t.Caption!.Contains("Largest individual"));
        largeTable.Should().NotBeNull("large arrays table must be emitted");
        largeTable!.Rows.Should().HaveCount(1);

        var sparseTable = tables.FirstOrDefault(t => t.Caption!.Contains("Sparse arrays"));
        sparseTable.Should().NotBeNull("sparse arrays table must be emitted");
        sparseTable!.Rows.Should().HaveCount(1);
    }

    // ── AppDomainSectionBuilder tests ─────────────────────────────────────────

    [Fact]
    public void AppDomainSectionBuilder_CanHandle_ReturnsTrueForAppDomainDomainResult()
    {
        var domain = Stamped(
            new AppDomainDomainResult(0, [], 0, 0, []),
            "AppDomain Analysis", "Modules");
        new AppDomainSectionBuilder().CanHandle(domain).Should().BeTrue();
    }

    [Fact]
    public void AppDomainSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new EventLeakDomainResult(0, 0, 0, 0),
            "Event Leak Analysis", "Events");
        new AppDomainSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void AppDomainSectionBuilder_Build_EmitsSummaryInventoryAndTypeDensityTables()
    {
        var domainSnapshot = new AppDomainSnapshot(
            Name: "DefaultDomain",
            Address: 0x1000_0000,
            DomainId: 1,
            ModuleCount: 42,
            EstimatedManagedBytes: 104_857_600);

        var moduleEntry = new ModuleTypeCountEntry(
            ModuleName: "MyApp.Core.dll",
            AssemblyName: "MyApp.Core, Version=1.0.0.0",
            TypeCount: 850,
            LiveTypeCount: 120,
            ObjectCount: 50_000,
            TotalBytes: 40_000_000);

        var domain = Stamped(
            new AppDomainDomainResult(
                TotalDomains: 1,
                Domains: [domainSnapshot],
                TotalDynamicModules: 3,
                AnonymousModuleCount: 1,
                TopModulesByTypeCount: [moduleEntry]),
            "AppDomain Analysis", "Modules");

        AnalyzerDetailSection section = new AppDomainSectionBuilder().Build(domain);

        section.AnalyzerName.Should().Be("AppDomain Analysis");
        section.SortOrder.Should().Be(41);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total AppDomains" && m.RawValue == 1);
        metrics.Should().Contain(m => m.Label == "Dynamic Modules");
        metrics.Should().Contain(m => m.Label == "Anonymous Modules");

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(2, "should emit domain inventory and type density tables");

        var inventoryTable = tables.FirstOrDefault(t => t.Caption!.Contains("AppDomain inventory"));
        inventoryTable.Should().NotBeNull("AppDomain inventory table must be emitted");
        inventoryTable!.Rows.Should().HaveCount(1);

        var typeTable = tables.FirstOrDefault(t => t.Caption!.Contains("type count"));
        typeTable.Should().NotBeNull("type density table must be emitted");
        typeTable!.Rows.Should().HaveCount(1);
    }

    // ── SegmentReservationSectionBuilder tests ────────────────────────────────

    [Fact]
    public void SegmentReservationSectionBuilder_CanHandle_ReturnsTrueForSegmentReservationDomainResult()
    {
        var result = Stamped(
            new SegmentReservationDomainResult(0, 0, 0, 0.0, 0, 0.0, 0, [], new Dictionary<int, ulong>(), false, string.Empty),
            "Segment Reservation Analysis", "Memory");
        new SegmentReservationSectionBuilder().CanHandle(result).Should().BeTrue();
    }

    [Fact]
    public void SegmentReservationSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new SegmentAnalysisDomainResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            "Segment Analysis", "Memory");
        new SegmentReservationSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void SegmentReservationSectionBuilder_Build_EmitsSummaryAndSegmentTable()
    {
        var entries = new List<SegmentReservationEntry>
        {
            new(0x1000_0000UL, HeapSegmentKind.SmallObjectHeap, 1024 * 1024, 4 * 1024 * 1024, true,  0, 25.0),
            new(0x2000_0000UL, HeapSegmentKind.LargeObjectHeap, 2 * 1024 * 1024, 8 * 1024 * 1024, false, 0, 0.0),
        };
        var byHeap = new Dictionary<int, ulong> { [0] = 12 * 1024 * 1024UL };
        var result = Stamped(
            new SegmentReservationDomainResult(
                TotalCommittedBytes: 3 * 1024 * 1024,
                TotalReservedBytes: 12 * 1024 * 1024,
                ReservationGapBytes: 9 * 1024 * 1024,
                ReservedToCommittedRatio: 4.0,
                EphemeralSegmentCount: 1,
                AvgEphemeralFillPct: 25.0,
                NonEphemeralSohSegmentCount: 0,
                SegmentTable: entries,
                ReservedByLogicalHeap: byHeap,
                AddressSpacePressureRisk: false,
                PressureRiskReason: string.Empty),
            "Segment Reservation Analysis", "Memory");

        AnalyzerDetailSection section = new SegmentReservationSectionBuilder().Build(result);

        section.AnalyzerName.Should().Be("Segment Reservation Analysis");
        section.SortOrder.Should().Be(36);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total committed");
        metrics.Should().Contain(m => m.Label == "Total reserved");
        metrics.Should().Contain(m => m.Label == "Avg ephemeral fill");

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(1, "segment table must be emitted");

        var segTable = tables.FirstOrDefault(t => t.Caption!.Contains("segments by reserved"));
        segTable.Should().NotBeNull("top-segments table must be emitted");
        segTable!.Rows.Should().HaveCount(2);
    }

    // ── WeakReferenceSectionBuilder tests ────────────────────────────────────

    [Fact]
    public void WeakReferenceSectionBuilder_CanHandle_ReturnsTrueForWeakReferenceDomainResult()
    {
        var result = Stamped(
            new WeakReferenceDomainResult(0, 0, 0, 0.0, 0, 0, 0, [], [], 0, false),
            "Weak Reference Analysis", "Memory");
        new WeakReferenceSectionBuilder().CanHandle(result).Should().BeTrue();
    }

    [Fact]
    public void WeakReferenceSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new SegmentAnalysisDomainResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            "Segment Analysis", "Memory");
        new WeakReferenceSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void WeakReferenceSectionBuilder_Build_EmitsSummaryAndTargetTable()
    {
        var topTargetTypes = new List<NameCountEntry>
        {
            new("System.String", 120),
            new("System.Object", 40),
        };
        var result = Stamped(
            new WeakReferenceDomainResult(
                TotalWeakHandles: 200,
                AliveWeakTargets: 80,
                DeadWeakTargets: 120,
                DeadTargetRatio: 0.6,
                WeakReferenceObjectCount: 50,
                WeakReferenceObjectBytes: 1024 * 1024,
                StaleWrapperCount: 10,
                TopWeakTargetTypes: topTargetTypes,
                TopStaleWrapperHolderTypes: [],
                DependentHandleDeadKeyCount: 5,
                ScanCapped: false),
            "Weak Reference Analysis", "Memory");

        AnalyzerDetailSection section = new WeakReferenceSectionBuilder().Build(result);

        section.AnalyzerName.Should().Be("Weak Reference Analysis");
        section.SortOrder.Should().Be(49);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total weak handles" && m.RawValue == 200);
        metrics.Should().Contain(m => m.Label == "Dead targets" && m.RawValue == 120);
        metrics.Should().Contain(m => m.Label == "Dead target ratio");
        metrics.Should().Contain(m => m.Label == "Stale wrappers (m_handle=0)");
        metrics.Should().Contain(m => m.Label == "Dependent handles with dead primary key" && m.RawValue == 5);

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(1, "top-target-types table must be emitted");

        var targetTable = tables.FirstOrDefault(t => t.Caption!.Contains("alive weak target"));
        targetTable.Should().NotBeNull("top alive weak target types table must be emitted");
        targetTable!.Rows.Should().HaveCount(2);
    }

    // ── BoxingSectionBuilder tests ────────────────────────────────────────────

    [Fact]
    public void BoxingSectionBuilder_CanHandle_ReturnsTrueForBoxingDomainResult()
    {
        var result = Stamped(
            new BoxingDomainResult(0, 0, [], 0, 0, 0, [], false),
            "Boxing Analysis", "Memory");
        new BoxingSectionBuilder().CanHandle(result).Should().BeTrue();
    }

    [Fact]
    public void BoxingSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new SegmentAnalysisDomainResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            "Segment Analysis", "Memory");
        new BoxingSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void BoxingSectionBuilder_Build_EmitsSummaryMetricsAndBoxedTypeTable()
    {
        var boxedTypes = new List<BoxedTypeEntry>
        {
            new("System.Int32",  5000, 80000, false),
            new("System.Status", 2000, 16000, true),
        };
        var paddingTypes = new List<StructPaddingEntry>
        {
            new("MyApp.BigStruct", 12, 24, 12, 0.5),
        };
        var result = Stamped(
            new BoxingDomainResult(
                TotalBoxedObjects: 7000,
                TotalBoxedBytes: 96000,
                TopBoxedTypes: boxedTypes,
                BoxedEnumCount: 2000,
                BoxedEnumBytes: 16000,
                OversizedValueTypeCount: 50,
                TopPaddingWasteTypes: paddingTypes,
                TypeScanCapped: false),
            "Boxing Analysis", "Memory");

        AnalyzerDetailSection section = new BoxingSectionBuilder().Build(result);

        section.AnalyzerName.Should().Be("Boxing Analysis");
        section.SortOrder.Should().Be(50);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total boxed objects" && m.RawValue == 7000);
        metrics.Should().Contain(m => m.Label == "Boxed enum instances" && m.RawValue == 2000);
        metrics.Should().Contain(m => m.Label == "Oversized value types" && m.RawValue == 50);

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(2, "boxed types and padding waste tables must be emitted");

        var boxedTable = tables.FirstOrDefault(t => t.Caption!.Contains("boxed types"));
        boxedTable.Should().NotBeNull("top boxed types table must be emitted");
        boxedTable!.Rows.Should().HaveCount(2);

        var padTable = tables.FirstOrDefault(t => t.Caption!.Contains("padding waste"));
        padTable.Should().NotBeNull("padding waste table must be emitted");
        padTable!.Rows.Should().HaveCount(1);
    }

    // ── JitSectionBuilder tests ───────────────────────────────────────────────

    [Fact]
    public void JitSectionBuilder_CanHandle_ReturnsTrueForJitDomainResult()
    {
        var result = Stamped(
            new JitDomainResult(0, 0, 0.0, 0, [], [], 0, 0, 0),
            "JIT Analysis", "Performance");
        new JitSectionBuilder().CanHandle(result).Should().BeTrue();
    }

    [Fact]
    public void JitSectionBuilder_CanHandle_ReturnsFalseForUnrelated()
    {
        var unrelated = Stamped(
            new BoxingDomainResult(0, 0, [], 0, 0, 0, [], false),
            "Boxing Analysis", "Memory");
        new JitSectionBuilder().CanHandle(unrelated).Should().BeFalse();
    }

    [Fact]
    public void JitSectionBuilder_Build_EmitsSummaryMetricsAndTables()
    {
        var methods = new List<JitMethodSnapshot>
        {
            new("MyApp.Foo.HeavyMethod()", "MyApp.Foo", 0x1000, 80_000, 0, false),
            new("MyApp.Bar.Process()",     "MyApp.Bar", 0x2000, 72_000, 5_000, false),
        };
        var frameTypes = new List<NameCountEntry>
        {
            new("MyApp.RequestHandler", 42),
            new("MyApp.DataProcessor",  18),
        };
        var result = Stamped(
            new JitDomainResult(
                TotalJitHeapBytes: 200 * 1024 * 1024,
                JitManagerCount: 2,
                JitHeapPctOfTotalProcess: 0.0,
                ActiveMethodsOnStacks: 100,
                TopLargestMethods: methods,
                TopActiveFrameTypes: frameTypes,
                UnmanagedFrameCount: 15,
                ManagedFrameCount: 85,
                TieredMethodCount: 3),
            "JIT Analysis", "Performance");

        AnalyzerDetailSection section = new JitSectionBuilder().Build(result);

        section.AnalyzerName.Should().Be("JIT Analysis");
        section.SortOrder.Should().Be(51);

        var metrics = section.Blocks.OfType<MetricBlock>().ToList();
        metrics.Should().Contain(m => m.Label == "Total JIT code heap");
        metrics.Should().Contain(m => m.Label == "JIT manager count" && m.RawValue == 2);
        metrics.Should().Contain(m => m.Label == "Active method instances on stacks" && m.RawValue == 100);

        var tables = section.Blocks.OfType<TableBlock>().ToList();
        tables.Should().HaveCountGreaterThanOrEqualTo(2, "active frame types and large methods tables must be emitted");

        var frameTypesTable = tables.FirstOrDefault(t => t.Caption!.Contains("frame types"));
        frameTypesTable.Should().NotBeNull("active frame types table must be emitted");
        frameTypesTable!.Rows.Should().HaveCount(2);

        var methodsTable = tables.FirstOrDefault(t => t.Caption!.Contains("method"));
        methodsTable.Should().NotBeNull("large methods table must be emitted");
        methodsTable!.Rows.Should().HaveCount(2);
    }
}


