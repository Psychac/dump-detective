using DumpDetective.Reporting.SynthesisRules.Insight;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Covers the 10 <c>*InsightRule</c> classes that replaced <c>InsightEngine</c>'s
/// <c>BaselineRuleGroup</c> (phase 5 step 6.2, docs/refactor/modularity/phase-5-step6-insight-engine-plan.md).
/// One class per rule, mirroring the scenarios <c>InsightEngineTests</c> would have covered had it
/// tested this group directly (it mostly didn't — see that doc's note on <c>InsightEngineTests.cs</c>
/// not being a complete baseline).
/// </summary>
public sealed class InsightBaselineRulesTests
{
    private static Observation Obs(
        string observationType,
        IReadOnlyDictionary<string, Measure> measures,
        IReadOnlyList<EntityRef>? subjects = null) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = subjects ?? [],
        When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
        Measures = measures,
        Provenance = new Provenance
        {
            Artifact = new ArtifactId("test-artifact"),
            AnalyzerKey = "TestAnalyzer",
            CapabilitiesUsed = new HashSet<Capability>(),
            Fidelity = FidelityLevel.Full,
        },
        Confidence = 1.0,
    };

    private static Measure Count(double v) => new(v, MeasureUnit.Count, MeasureSemantics.Absolute);
    private static Measure Bytes(double v) => new(v, MeasureUnit.Bytes, MeasureSemantics.Absolute);
    private static Measure Pct(double v) => new(v, MeasureUnit.Percent, MeasureSemantics.Ratio);

    public sealed class LohPressure
    {
        private readonly LohPressureInsightRule _rule = new();

        [Fact]
        public async Task PrefersMemoryOverGcGenAndSegments_AndFiresCritical()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.memory-composition", new Dictionary<string, Measure> { ["loh.percent"] = Pct(45.0), ["loh.bytes"] = Bytes(450) }),
                Obs("gc.generation-composition", new Dictionary<string, Measure> { ["loh.percent"] = Pct(5.0), ["loh.bytes"] = Bytes(5) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Title.Should().Contain("LOH");
        }

        [Fact]
        public async Task BelowWarningThreshold_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.memory-composition", new Dictionary<string, Measure> { ["loh.percent"] = Pct(10.0), ["loh.bytes"] = Bytes(10) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class LohFragmentation
    {
        private readonly LohFragmentationInsightRule _rule = new();

        [Fact]
        public async Task AboveCriticalThreshold_FiresCritical()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.loh-fragmentation-summary", new Dictionary<string, Measure>
                {
                    ["fragmentation.percent"] = Pct(70.0),
                    ["free.bytes"] = Bytes(1024 * 1024),
                    ["free-block.count"] = Count(5),
                    ["largest-free-block.bytes"] = Bytes(512 * 1024),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Narrative.Should().Contain("5 blocks");
        }
    }

    public sealed class PohGrowth
    {
        private readonly PohGrowthInsightRule _rule = new();

        [Fact]
        public async Task AboveThreshold_FiresWarning()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("heap.topology-population", new Dictionary<string, Measure> { ["poh.percent"] = Pct(15.0), ["poh.bytes"] = Bytes(1024) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Warning);
        }
    }

    public sealed class ThreadContention
    {
        private readonly ThreadContentionInsightRule _rule = new();

        [Fact]
        public async Task HangBranch_TakesPriorityOverThreadFallback()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.hang-pressure", new Dictionary<string, Measure> { ["waiting.percent"] = Pct(80.0), ["waiting-thread.count"] = Count(8) }),
                Obs("runtime.thread-triage", new Dictionary<string, Measure>
                {
                    ["alive-thread.count"] = Count(10),
                    ["blocked-thread.count"] = Count(1),
                    ["lock-holding-thread.count"] = Count(2),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Title.Should().Contain("waiting");
            findings[0].Severity.Should().Be(Severity.Critical);
        }

        [Fact]
        public async Task BlockedFinalizerThread_EmitsSeparateCriticalFinding()
        {
            ThreadRef finalizerThreadRef = new() { OsThreadId = 42, Fidelity = MatchFidelity.Exact };
            var matched = new ObservationMatchSet(
            [
                Obs("runtime.thread-triage", new Dictionary<string, Measure>
                {
                    ["alive-thread.count"] = Count(10),
                    ["blocked-thread.count"] = Count(1),
                    ["lock-holding-thread.count"] = Count(0),
                }),
                Obs("runtime.finalizer-thread",
                    new Dictionary<string, Measure> { ["is-blocked"] = Count(1), ["locks-held.count"] = Count(3) },
                    subjects: [finalizerThreadRef]),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle(f => f.Title == "Finalizer thread is blocked" && f.Severity == Severity.Critical);
            findings[0].Narrative.Should().Contain("OS tid: 42");
        }
    }

    public sealed class FinalizerQueueBacklog
    {
        private readonly FinalizerQueueBacklogInsightRule _rule = new();

        [Fact]
        public async Task BlockedFinalizerWithRetainedBytes_EscalatesToCritical()
        {
            ThreadRef finalizerThreadRef = new() { OsThreadId = 7, Fidelity = MatchFidelity.Exact };
            var matched = new ObservationMatchSet(
            [
                Obs("finalizer.queue-population", new Dictionary<string, Measure> { ["queue.count"] = Count(2_000), ["retained.bytes"] = Bytes(1024) }),
                Obs("runtime.finalizer-thread",
                    new Dictionary<string, Measure> { ["is-blocked"] = Count(1), ["locks-held.count"] = Count(1) },
                    subjects: [finalizerThreadRef]),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
            findings[0].Narrative.Should().Contain("starvation risk");
        }
    }

    public sealed class PinnedHandlePressure
    {
        private readonly PinnedHandlePressureInsightRule _rule = new();

        [Fact]
        public async Task CorrelatesWithLohFragmentation_EscalatesToCritical()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("handle.population", new Dictionary<string, Measure> { ["pinned-target.count"] = Count(500) }),
                Obs("heap.loh-fragmentation-summary", new Dictionary<string, Measure>
                {
                    ["fragmentation.percent"] = Pct(40.0),
                    ["free.bytes"] = Bytes(0),
                    ["free-block.count"] = Count(0),
                    ["largest-free-block.bytes"] = Bytes(0),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Severity.Should().Be(Severity.Critical);
        }
    }

    public sealed class ActiveCrash
    {
        private readonly ActiveCrashInsightRule _rule = new();

        [Fact]
        public async Task GroupsActiveExceptionsByType_ReportsDominant()
        {
            var oomType = new TypeRef { CanonicalName = "System.OutOfMemoryException", Fidelity = MatchFidelity.Exact };
            var otherType = new TypeRef { CanonicalName = "System.InvalidOperationException", Fidelity = MatchFidelity.Exact };

            var matched = new ObservationMatchSet(
            [
                Obs("runtime.crash-population", new Dictionary<string, Measure> { ["active.count"] = Count(3), ["total.count"] = Count(3), ["unique-types.count"] = Count(2) }),
                Obs("runtime.active-exception", new Dictionary<string, Measure>(), subjects: [oomType]),
                Obs("runtime.active-exception", new Dictionary<string, Measure>(), subjects: [oomType]),
                Obs("runtime.active-exception", new Dictionary<string, Measure>(), subjects: [otherType]),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Narrative.Should().Contain("System.OutOfMemoryException (2 active)");
            findings[0].Severity.Should().Be(Severity.Critical);
        }
    }

    public sealed class LeakSuspicion
    {
        private readonly LeakSuspicionInsightRule _rule = new();

        [Fact]
        public async Task BothSignalsPresent_EmitsTwoFindings()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("type.retention-population", new Dictionary<string, Measure> { ["highly-referenced.count"] = Count(50) }),
                Obs("type.string-composition", new Dictionary<string, Measure>
                {
                    ["duplicate-wasted.bytes"] = Bytes(20 * 1024 * 1024),
                    ["duplicate-pattern.count"] = Count(10),
                    ["total-string.count"] = Count(1000),
                    ["total-string-memory.bytes"] = Bytes(5 * 1024 * 1024),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().HaveCount(2);
        }
    }

    public sealed class WastefulCollections
    {
        private readonly WastefulCollectionsInsightRule _rule = new();

        [Fact]
        public async Task ZeroWastefulCollections_ReturnsEmpty()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("collection.waste-summary", new Dictionary<string, Measure>
                {
                    ["wasteful-collection.count"] = Count(0), ["total-wasted.bytes"] = Bytes(0), ["total-collection.count"] = Count(100),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().BeEmpty();
        }
    }

    public sealed class OrphanedTaskAccumulation
    {
        private readonly OrphanedTaskAccumulationInsightRule _rule = new();

        [Fact]
        public async Task HighOrphanRatio_EmitsFinding()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("async.task-population", new Dictionary<string, Measure>
                {
                    ["faulted.count"] = Count(0), ["total.count"] = Count(100), ["orphaned.count"] = Count(60),
                }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle();
            findings[0].Title.Should().Contain("orphaned tasks");
        }

        [Fact]
        public async Task FaultedTasksWithBlockedFinalizer_EmitsFinding()
        {
            var matched = new ObservationMatchSet(
            [
                Obs("async.task-population", new Dictionary<string, Measure>
                {
                    ["faulted.count"] = Count(5), ["total.count"] = Count(10), ["orphaned.count"] = Count(0),
                }),
                Obs("runtime.finalizer-thread", new Dictionary<string, Measure> { ["is-blocked"] = Count(1), ["locks-held.count"] = Count(1) }),
            ]);

            IReadOnlyList<Finding> findings = await _rule.SynthesizeAsync(matched, new SynthesisContext());

            findings.Should().ContainSingle(f => f.Title.Contains("Faulted tasks"));
        }
    }
}
