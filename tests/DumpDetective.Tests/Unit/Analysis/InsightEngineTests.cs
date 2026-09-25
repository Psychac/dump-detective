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
    // Analyze_ShouldEmitWarning_WhenThreeAnalyzersFail removed 2026-09-23 — DetectAnalyzerFailures
    // migrated to AnalyzerFailuresInsightRule (phase 5 step 6.2), covered by
    // InsightBaselineRulesTests.AnalyzerFailures instead.

    // Analyze_ShouldEmitCritical_WhenFatalExceptionTypePresent moved to
    // FatalExceptionOnHeapInsightRuleTests (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) — DetectFatalExceptionOnHeap
    // was migrated out of InsightEngine and removed.

    // Analyze_ShouldEmitCritical_WhenLohPressureExceedsThreshold moved to
    // LohPressureInsightRuleTests (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) — DetectLohPressure was
    // migrated out of InsightEngine and removed, so exercising it via InsightEngine.Analyze is no
    // longer possible.

    // Analyze_ShouldEmitCorrelation_WhenDominantClusterOverlapsHangWaitingThreads /
    // Analyze_ShouldNotEmitCorrelation_WhenClusterAndHangThreadsDoNotOverlap removed 2026-09-23 —
    // DetectClusterHangCorrelation migrated to ClusterHangCorrelationInsightRule (phase 5 step 6.2),
    // covered by InsightCorrelationRulesTests.ClusterHangCorrelation instead.

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

    // Analyze_ShouldEmitWarning_WhenActiveExceptionsConcentratedInConflictedModule /
    // Analyze_ShouldNotEmitCrashModuleFinding_WhenActiveExceptionsBelowThreshold / CrashCandidate
    // helper removed 2026-09-23 — DetectCrashModuleHotspot migrated to
    // CrashModuleHotspotInsightRule (phase 5 step 6.2), covered by
    // InsightCorrelationRulesTests.CrashModuleHotspot instead.

    // Analyze_ShouldEmitEvidenceTable_WhenClusterOverlapsWithHangWaitReasons /
    // Analyze_ShouldEnrichClusterHangCorrelation_WithStackRootCounts_WhenThreadDataAvailable /
    // BuildLowSeverityClusterHangOverlap / Analyze_ShouldEscalateClusterHangCorrelation_.../
    // Analyze_ShouldNotEscalateClusterHangCorrelation_... removed 2026-09-23 —
    // DetectClusterHangCorrelation migrated to ClusterHangCorrelationInsightRule (phase 5 step 6.2),
    // covered by InsightCorrelationRulesTests.ClusterHangCorrelation instead.

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

    // Analyze_ShouldEmitPinnedStringLeak_WhenPinnedStringBytesExceedThreshold /
    // Analyze_ShouldNotEmitPinnedStringLeak_WhenPinnedStringBytesBelowThreshold removed 2026-09-22 —
    // DetectPinnedStringLeak migrated to PinnedStringLeakInsightRule (phase 5 step 6.2), covered by
    // InsightCorrelationRulesTests.PinnedStringLeak instead.

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

    // Analyze_ShouldEmitCorrelation_WhenStaticRootRetainsTypeQueuedForFinalization /
    // Analyze_ShouldNotEmitCorrelation_WhenNoRetainedTypeMatchesQueuedType removed 2026-09-23 —
    // DetectStaticRootFinalizableCorrelation migrated to StaticRootFinalizableCorrelationInsightRule
    // (phase 5 step 6.2), covered by InsightCorrelationRulesTests.StaticRootFinalizableCorrelation
    // instead.

    // Analyze_ShouldEmitFinding_WhenActiveTransactionsHoldOpenConnections /
    // Analyze_ShouldNotEmitFinding_WhenTransactionConnectionsDoNotMatchOpenConnections removed
    // 2026-09-23 — DetectLongHeldTransactionOnOpenConnection migrated to
    // LongHeldTransactionOnOpenConnectionInsightRule (phase 5 step 6.2), covered by
    // InsightCorrelationRulesTests.LongHeldTransactionOnOpenConnection instead.

    // Analyze_ShouldEmitFinding_WhenOpeningChannelsCoincideWithTimeoutExceptions,
    // Analyze_ShouldEscalateToWarning_WhenOpeningChannelCountIsHigh,
    // Analyze_ShouldNotEmitOpeningTimeoutFinding_WhenNoTimeoutExceptionsPresent, and
    // Analyze_ShouldNotEmitOpeningTimeoutFinding_WhenNoChannelsAreOpening moved to
    // WcfOpeningTimeoutCorrelationInsightRuleTests (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) -
    // DetectWcfOpeningTimeoutCorrelation was migrated out of InsightEngine and removed.

    // Analyze_ReferenceChainTypeBelowHeapFractionThreshold_EmitsNoDominatorCorrelationFinding,
    // Analyze_ReferenceChainTypeAboveWarningThreshold_EmitsWarningDominatorCorrelation, and
    // Analyze_ReferenceChainTypeAboveCriticalThreshold_EmitsCriticalDominatorCorrelation moved to
    // ReferenceChainDominatorCorrelationInsightRuleTests (phase 5 step 6.2,
    // docs/refactor/modularity/phase-5-step6-insight-engine-plan.md) -
    // DetectReferenceChainDominatorCorrelation was migrated out of InsightEngine and removed
    // (without its evidence table — see the new rule's own remarks). BuildReferenceChainResult/
    // BuildDominatorResultWithTotalHeap, only used by those three tests, removed with them.

    private static AnalyzerRunResult BuildRun(string analyzerName, AnalyzerExecutionStatus status, AnalyzerDomainResult? result = null)
        => new(
            AnalyzerName: analyzerName,
            Status: status,
            Duration: TimeSpan.Zero,
            Result: result,
            ErrorMessage: status == AnalyzerExecutionStatus.Failed ? "failed" : null,
            ErrorType: status == AnalyzerExecutionStatus.Failed ? nameof(InvalidOperationException) : null);
}
