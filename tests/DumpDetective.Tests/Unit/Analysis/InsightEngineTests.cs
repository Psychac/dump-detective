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

    // Analyze_ShouldEmitCrossAnalyzerFinding_WithEvidenceTable_WhenJitHotspotModuleIsConflicted
    // removed 2026-09-26 — DetectJitModuleHotspot migrated to JitModuleHotspotInsightRule (phase 5
    // step 6.2), covered by InsightMemoryAndRuntimeRulesTests.JitModuleHotspot instead.

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

    // Analyze_ShouldEmitEvidenceTable_WhenSystemStringIsTopMemoryTypeWithDuplication /
    // Analyze_ShouldNotEmitStringMemoryConcentration_WhenStringIsNotAmongTopMemoryTypes removed
    // 2026-09-26 — DetectStringMemoryConcentration dropped, not migrated (phase 5 step 6.2).

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
