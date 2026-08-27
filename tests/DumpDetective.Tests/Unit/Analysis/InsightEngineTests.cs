using DumpDetective.Analysis.Insight;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;

using System.Linq;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class InsightEngineTests
{
    [Fact]
    public void Analyze_ShouldEmitWarning_WhenThreeAnalyzersFail()
    {
        InsightEngine engine = new();

        AnalyzerRunResult[] runs =
        [
            BuildRun("A", AnalyzerExecutionStatus.Failed),
            BuildRun("B", AnalyzerExecutionStatus.Failed),
            BuildRun("C", AnalyzerExecutionStatus.Failed),
            BuildRun("D", AnalyzerExecutionStatus.Success, new GenericAnalyzerDomainResult())
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Warning
            && f.Title.Contains("analyzer(s) failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCritical_WhenFatalExceptionTypePresent()
    {
        InsightEngine engine = new();

        CrashDomainResult crash = new(
            TotalExceptions: 3,
            ActiveExceptions: 0,
            ExceptionTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["System.OutOfMemoryException"] = 1,
                ["System.InvalidOperationException"] = 2,
            },
            ActiveExceptionTypeCounts: new Dictionary<string, int>(StringComparer.Ordinal));

        AnalyzerRunResult[] runs =
        [
            BuildRun("Crash Analyzer", AnalyzerExecutionStatus.Success, crash)
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Critical
            && f.Title.Contains("Fatal exception", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCritical_WhenLohPressureExceedsThreshold()
    {
        InsightEngine engine = new();

        MemoryDomainResult memory = new(
            TotalBytes: 1_000,
            LohBytes: 450,
            LohPercent: 45.0,
            TotalObjects: 10,
            LohObjects: 1,
            LohThresholdBytes: 85_000,
            UniqueTypes: 3,
            TopTypes: []);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analyzer", AnalyzerExecutionStatus.Success, memory)
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Severity == FindingSeverity.Critical
            && f.Title.Contains("LOH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCorrelation_WhenDominantClusterOverlapsHangWaitingThreads()
    {
        InsightEngine engine = new();

        ThreadStackClusterDomainResult clusters = new(
            AliveThreadCount: 10,
            UniqueClusters: 2,
            SingletonSignatures: 0,
            DiversityPercent: 20.0,
            TopClusterSignatures: ["sig-a"],
            TopClusters:
            [
                new ThreadClusterSnapshot(
                    Count: 8,
                    SampleOsThreadIds: [1, 2, 3, 4, 5],
                    Signature: "sig-a")
            ]);

        HangDomainResult hang = new(
            TotalAliveThreads: 10,
            WaitingThreadCount: 5,
            ThreadsHoldingLocks: 0,
            WaitingPercent: 50.0,
            WaitCategoryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            TotalTaskContinuations: 0,
            QueuedWorkItems: 0,
            TotalTasks: 0,
            PendingTasks: 0,
            FaultedTasks: 0,
            CanceledTasks: 0,
            RuntimeThreadPoolDataAvailable: false,
            RuntimeMinThreads: 0,
            RuntimeMaxThreads: 0,
            RuntimeActiveWorkerThreads: 0,
            RuntimeIdleWorkerThreads: 0,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeQueueLength: null,
            RuntimeCpuUtilization: 0,
            IsStarved: false,
            HealthScore: 50,
            TopWaitingThreads:
            [
                new WaitingThreadSnapshot(1, 1, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(2, 2, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(3, 3, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(4, 4, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
            ]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Thread Stack Signature Clustering", AnalyzerExecutionStatus.Success, clusters),
            BuildRun("Hang Analyzer", AnalyzerExecutionStatus.Success, hang),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("HangAnalyzer", StringComparison.OrdinalIgnoreCase)
            && f.Title.Contains("cluster", StringComparison.OrdinalIgnoreCase)
            && f.Evidence.Contains("Monitor.Wait", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ShouldNotEmitCorrelation_WhenClusterAndHangThreadsDoNotOverlap()
    {
        InsightEngine engine = new();

        ThreadStackClusterDomainResult clusters = new(
            AliveThreadCount: 10,
            UniqueClusters: 2,
            SingletonSignatures: 0,
            DiversityPercent: 20.0,
            TopClusterSignatures: ["sig-a"],
            TopClusters:
            [
                new ThreadClusterSnapshot(
                    Count: 8,
                    SampleOsThreadIds: [101, 102, 103, 104, 105],
                    Signature: "sig-a")
            ]);

        HangDomainResult hang = new(
            TotalAliveThreads: 10,
            WaitingThreadCount: 5,
            ThreadsHoldingLocks: 0,
            WaitingPercent: 50.0,
            WaitCategoryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            TotalTaskContinuations: 0,
            QueuedWorkItems: 0,
            TotalTasks: 0,
            PendingTasks: 0,
            FaultedTasks: 0,
            CanceledTasks: 0,
            RuntimeThreadPoolDataAvailable: false,
            RuntimeMinThreads: 0,
            RuntimeMaxThreads: 0,
            RuntimeActiveWorkerThreads: 0,
            RuntimeIdleWorkerThreads: 0,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeQueueLength: null,
            RuntimeCpuUtilization: 0,
            IsStarved: false,
            HealthScore: 50,
            TopWaitingThreads:
            [
                new WaitingThreadSnapshot(1, 1, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
                new WaitingThreadSnapshot(2, 2, "WaitForSingleObject", "Monitor.Wait", 0, "frame1"),
            ]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Thread Stack Signature Clustering", AnalyzerExecutionStatus.Success, clusters),
            BuildRun("Hang Analyzer", AnalyzerExecutionStatus.Success, hang),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f =>
            f.Title.Contains("HangAnalyzer", StringComparison.OrdinalIgnoreCase)
            && f.Title.Contains("cluster", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitCrossAnalyzerFinding_WithEvidenceTable_WhenJitHotspotModuleIsConflicted()
    {
        InsightEngine engine = new();

        JitDomainResult jit = new(
            TotalJitHeapBytes: 1_000_000,
            JitManagerCount: 1,
            ActiveMethodsOnStacks: 500,
            DistinctMethodsOnStacks: 20,
            TopLargestMethods: [],
            TopActiveFrameTypes: [],
            TopActiveModulesByFrameHits: [new NameCountEntry("MyApp.Plugins.dll", 200)],
            UnmanagedFrameCount: 0,
            ManagedFrameCount: 500,
            ReadyToRunFrameCount: 0,
            DynamicMethodFrameCount: 0,
            TieredMethodCount: 0,
            MaxThreadFrameDepth: 0,
            MaxThreadFrameDepthOSThreadId: 0,
            LargeMethodThresholdBytes: 64 * 1024);

        ModuleDomainResult modules = new(
            TotalModules: 10,
            DynamicModules: 0,
            UniqueModuleNames: 10,
            VersionConflictGroups: 1,
            ConflictingAssemblyNames: ["MyApp.Plugins.dll"],
            TopModulesBySize: [new LoadedModuleSnapshot("MyApp.Plugins.dll", "MyApp.Plugins", "C:\\app\\MyApp.Plugins.dll", 0x1000, 5_000_000, false, true)],
            ConflictDetails: [],
            HeavyModuleWarningThresholdBytes: 1_000_000,
            UnknownIdentityDuplicateModules: new HashSet<string>());

        AnalyzerRunResult[] runs =
        [
            BuildRun("JIT Analysis", AnalyzerExecutionStatus.Success, jit),
            BuildRun("Module Analysis", AnalyzerExecutionStatus.Success, modules),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("heavy active JIT stack presence", StringComparison.OrdinalIgnoreCase)
            && f.Tags.Contains("cross-analyzer")
            && f.EffectiveEvidenceTables.Count == 1
            && f.EffectiveEvidenceTables[0].Rows.Count == 1);
    }

    [Fact]
    public void Analyze_ShouldEmitEvidenceTable_WhenClusterOverlapsWithHangWaitReasons()
    {
        InsightEngine engine = new();

        ThreadStackClusterDomainResult clusters = new(
            AliveThreadCount: 10,
            UniqueClusters: 1,
            SingletonSignatures: 0,
            DiversityPercent: 10.0,
            TopClusterSignatures: ["Frame.A -> Frame.B"],
            TopClusters: [new ThreadClusterSnapshot(
                Count: 8,
                SampleOsThreadIds: [1, 2, 3, 4],
                Signature: "Frame.A -> Frame.B")]);

        HangDomainResult hang = new(
            TotalAliveThreads: 10,
            WaitingThreadCount: 4,
            ThreadsHoldingLocks: 0,
            WaitingPercent: 40.0,
            WaitCategoryBreakdown: new Dictionary<string, int>(),
            TotalTaskContinuations: 0,
            QueuedWorkItems: 0,
            TotalTasks: 0,
            PendingTasks: 0,
            FaultedTasks: 0,
            CanceledTasks: 0,
            RuntimeThreadPoolDataAvailable: false,
            RuntimeMinThreads: 0,
            RuntimeMaxThreads: 0,
            RuntimeActiveWorkerThreads: 0,
            RuntimeIdleWorkerThreads: 0,
            RuntimeRetiredWorkerThreads: 0,
            RuntimeQueueLength: null,
            RuntimeCpuUtilization: 0,
            IsStarved: false,
            HealthScore: 50,
            TopWaitingThreads:
            [
                new WaitingThreadSnapshot(1, 1, "Monitor", "MonitorWait", 0, "Frame.A"),
                new WaitingThreadSnapshot(2, 2, "Monitor", "MonitorWait", 0, "Frame.A"),
                new WaitingThreadSnapshot(3, 3, "Monitor", "MonitorWait", 0, "Frame.A"),
                new WaitingThreadSnapshot(4, 4, "SqlWait", "UserRequestWait", 0, "Frame.A"),
            ]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Thread Stack Signature Clustering", AnalyzerExecutionStatus.Success, clusters),
            BuildRun("Hang Analyzer", AnalyzerExecutionStatus.Success, hang),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("Dominant thread-stack cluster correlates", StringComparison.OrdinalIgnoreCase)
            && f.EffectiveEvidenceTables.Count == 1
            && f.EffectiveEvidenceTables[0].Rows.Count == 2
            && f.EffectiveEvidenceTables[0].Rows[0].SequenceEqual(new object?[] { "MonitorWait", 3 }));
    }

    [Fact]
    public void Analyze_ShouldEmitEvidenceTable_WhenTopMemoryTypeIsAlmostEntirelyGen2()
    {
        InsightEngine engine = new();

        MemoryDomainResult memory = new(
            TotalBytes: 200_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 1_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: [new TypeSnapshot("MyApp.Cache.Entry", 1_000, 150_000_000, 0)]);

        GCGenerationDomainResult gcGen = new(
            Gen0Bytes: 0, Gen0Objects: 0,
            Gen1Bytes: 0, Gen1Objects: 0,
            Gen2Bytes: 150_000_000, Gen2Objects: 950,
            LohBytes: 0, LohPercent: 0,
            TotalObjects: 1_000, LohObjects: 0,
            TopLohTypes: [],
            PerTypeGenerationProfiles: [new TypeGenerationProfile("MyApp.Cache.Entry", 10, 40, 950, 0, 150_000_000)]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analysis", AnalyzerExecutionStatus.Success, memory),
            BuildRun("GC Generation Analysis", AnalyzerExecutionStatus.Success, gcGen),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("long-lived", StringComparison.OrdinalIgnoreCase)
            && f.Tags.Contains("gc-generation")
            && f.EffectiveEvidenceTables.Count == 1
            && f.EffectiveEvidenceTables[0].Rows.Count == 1
            && f.EffectiveEvidenceTables[0].Rows[0][0]!.Equals("MyApp.Cache.Entry"));
    }

    [Fact]
    public void Analyze_ShouldNotEmitMemoryGenerationCorrelation_WhenTypeIsMostlyGen0()
    {
        InsightEngine engine = new();

        MemoryDomainResult memory = new(
            TotalBytes: 200_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 1_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 1,
            TopTypes: [new TypeSnapshot("MyApp.Transient.Buffer", 1_000, 150_000_000, 0)]);

        GCGenerationDomainResult gcGen = new(
            Gen0Bytes: 150_000_000, Gen0Objects: 950,
            Gen1Bytes: 0, Gen1Objects: 0,
            Gen2Bytes: 0, Gen2Objects: 10,
            LohBytes: 0, LohPercent: 0,
            TotalObjects: 1_000, LohObjects: 0,
            TopLohTypes: [],
            PerTypeGenerationProfiles: [new TypeGenerationProfile("MyApp.Transient.Buffer", 950, 40, 10, 0, 150_000_000)]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analysis", AnalyzerExecutionStatus.Success, memory),
            BuildRun("GC Generation Analysis", AnalyzerExecutionStatus.Success, gcGen),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f => f.Title.Contains("long-lived", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitEvidenceTable_WhenSystemStringIsTopMemoryTypeWithDuplication()
    {
        InsightEngine engine = new();

        MemoryDomainResult memory = new(
            TotalBytes: 200_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 10_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: 2,
            TopTypes:
            [
                new TypeSnapshot("MyApp.Cache.Entry", 1_000, 150_000_000, 0),
                new TypeSnapshot("System.String", 8_000, 40_000_000, 0),
            ]);

        StringDomainResult strings = new(
            TotalStrings: 8_000,
            TotalStringMemoryBytes: 40_000_000,
            SampledUniquePatterns: 1_000,
            DuplicatePatternCount: 500,
            DuplicateWastedBytes: 8_000_000,
            DuplicationRatio: 0.88,
            PctOfManagedHeap: 20.0,
            TopDuplicates: [new DuplicateStringSnapshot("connection-string-template", 3_000, 6_000_000)],
            VeryLongStrings: [],
            LohStringBytes: 0,
            InternedStringCount: 0,
            InternedStringBytes: 0,
            Gen0StringCount: 0,
            Gen1StringCount: 0,
            Gen2StringCount: 0,
            Gen2StringBytes: 0,
            StringsSampled: 8_000);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analysis", AnalyzerExecutionStatus.Success, memory),
            BuildRun("String Analysis", AnalyzerExecutionStatus.Success, strings),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("String data is a top heap consumer", StringComparison.OrdinalIgnoreCase)
            && f.Tags.Contains("strings")
            && f.EffectiveEvidenceTables.Count == 1
            && f.EffectiveEvidenceTables[0].Rows.Count == 1
            && f.EffectiveEvidenceTables[0].Rows[0][0]!.Equals("connection-string-template"));
    }

    [Fact]
    public void Analyze_ShouldNotEmitStringMemoryConcentration_WhenStringIsNotAmongTopMemoryTypes()
    {
        InsightEngine engine = new();

        var topTypes = new List<TypeSnapshot>();
        for (int i = 0; i < 15; i++)
            topTypes.Add(new TypeSnapshot($"MyApp.Type{i}", 100, 1_000_000, 0));

        MemoryDomainResult memory = new(
            TotalBytes: 200_000_000,
            LohBytes: 0,
            LohPercent: 0,
            TotalObjects: 10_000,
            LohObjects: 0,
            LohThresholdBytes: 85_000,
            UniqueTypes: topTypes.Count,
            TopTypes: topTypes);

        StringDomainResult strings = new(
            TotalStrings: 8_000,
            TotalStringMemoryBytes: 40_000_000,
            SampledUniquePatterns: 1_000,
            DuplicatePatternCount: 500,
            DuplicateWastedBytes: 8_000_000,
            DuplicationRatio: 0.88,
            PctOfManagedHeap: 20.0,
            TopDuplicates: [new DuplicateStringSnapshot("connection-string-template", 3_000, 6_000_000)],
            VeryLongStrings: [],
            LohStringBytes: 0,
            InternedStringCount: 0,
            InternedStringBytes: 0,
            Gen0StringCount: 0,
            Gen1StringCount: 0,
            Gen2StringCount: 0,
            Gen2StringBytes: 0,
            StringsSampled: 8_000);

        AnalyzerRunResult[] runs =
        [
            BuildRun("Memory Analysis", AnalyzerExecutionStatus.Success, memory),
            BuildRun("String Analysis", AnalyzerExecutionStatus.Success, strings),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f => f.Title.Contains("String data is a top heap consumer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldEmitPinnedStringLeak_WhenPinnedStringBytesExceedThreshold()
    {
        InsightEngine engine = new();

        GCHandleDomainResult handles = new(
            TotalHandles: 10_000,
            StrongLikeHandles: 9_000,
            WeakLikeHandles: 900,
            PinnedHandleTargets: 100,
            TopPinnedTargetTypes: [new NameCountEntry("System.String", 50)],
            TopPinnedObjectsBySize: [new NameBytesEntry("System.String", 5_000_000)]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("GC Handle Analysis", AnalyzerExecutionStatus.Success, handles),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().Contain(f =>
            f.Title.Contains("Pinned strings detected", StringComparison.OrdinalIgnoreCase)
            && f.Tags.Contains("strings")
            && f.Tags.Contains("pinning"));
    }

    [Fact]
    public void Analyze_ShouldNotEmitPinnedStringLeak_WhenPinnedStringBytesBelowThreshold()
    {
        InsightEngine engine = new();

        GCHandleDomainResult handles = new(
            TotalHandles: 10_000,
            StrongLikeHandles: 9_000,
            WeakLikeHandles: 900,
            PinnedHandleTargets: 5,
            TopPinnedTargetTypes: [new NameCountEntry("System.String", 2)],
            TopPinnedObjectsBySize: [new NameBytesEntry("System.String", 100)]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("GC Handle Analysis", AnalyzerExecutionStatus.Success, handles),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f => f.Title.Contains("Pinned strings detected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ShouldNotEmitPinnedStringLeak_WhenNoStringsArePinned()
    {
        InsightEngine engine = new();

        GCHandleDomainResult handles = new(
            TotalHandles: 10_000,
            StrongLikeHandles: 9_000,
            WeakLikeHandles: 900,
            PinnedHandleTargets: 200,
            TopPinnedTargetTypes: [new NameCountEntry("MyApp.Buffer", 200)],
            TopPinnedObjectsBySize: [new NameBytesEntry("MyApp.Buffer", 50_000_000)]);

        AnalyzerRunResult[] runs =
        [
            BuildRun("GC Handle Analysis", AnalyzerExecutionStatus.Success, handles),
        ];

        IReadOnlyList<InsightFinding> findings = engine.Analyze(runs);

        findings.Should().NotContain(f => f.Title.Contains("Pinned strings detected", StringComparison.OrdinalIgnoreCase));
    }

    private static AnalyzerRunResult BuildRun(string analyzerName, AnalyzerExecutionStatus status, AnalyzerDomainResult? result = null)
        => new(
            AnalyzerName: analyzerName,
            Status: status,
            Duration: TimeSpan.Zero,
            Result: result,
            ErrorMessage: status == AnalyzerExecutionStatus.Failed ? "failed" : null,
            ErrorType: status == AnalyzerExecutionStatus.Failed ? nameof(InvalidOperationException) : null);
}
