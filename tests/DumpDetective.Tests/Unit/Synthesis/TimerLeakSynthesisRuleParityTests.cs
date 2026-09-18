using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;
using DumpDetective.Reporting.SynthesisRules;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// Field-for-field parity between <see cref="TimerLeakFindingGenerator"/> (old) and
/// <see cref="TimerLeakSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class TimerLeakSynthesisRuleParityTests
{
    private readonly TimerLeakFindingGenerator _oldGenerator = new();
    private readonly TimerLeakSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoTimersFound_ReturnsEmpty() => AssertParity([]);

    [Fact]
    public Task LogicalTimerCountBelowWarningThreshold_NoFinding() => AssertParity(
        [("System.Threading.TimerQueueTimer", 50)]);

    [Fact]
    public Task LogicalTimerCountAtWarningThreshold() => AssertParity(
        [("System.Threading.TimerQueueTimer", 100)]);

    [Fact]
    public Task LogicalTimerCountAtCriticalThreshold() => AssertParity(
        [("System.Threading.TimerQueueTimer", 250)]);

    [Fact]
    public Task AllSixCategoriesRepresented() => AssertParity(
    [
        ("System.Threading.Timer", 10),
        ("System.Timers.Timer", 5),
        ("System.Threading.TimerQueueTimer", 100),
        ("System.Threading.TimerHolder", 20),
        ("System.Threading.PeriodicTimer", 3),
        ("MyApp.CustomTimerWrapper", 7),
    ]);

    [Fact]
    public Task QueuePressureAtInfoThreshold() => AssertParity(
        [("System.Threading.TimerHolder", 50)]);

    [Fact]
    public Task QueuePressureAtWarningThreshold() => AssertParity(
        [("System.Threading.TimerHolder", 100), ("System.Threading.TimerQueueTimer", 50)]);

    [Fact]
    public Task QueuePressureBelowThreshold_NoFinding() => AssertParity(
        [("System.Threading.TimerHolder", 20)]);

    [Fact]
    public Task BothFindingsFireTogether() => AssertParity(
        [("System.Threading.TimerQueueTimer", 300), ("System.Threading.TimerHolder", 100)]);

    [Fact]
    public Task OtherTimerCategoryOnly_NoFindings() => AssertParity(
        [("MyApp.CustomTimerWrapper", 5)]);

    private async Task AssertParity(IReadOnlyList<(string TypeName, int Count)> byType)
    {
        TimerLeakDomainResult result = BuildResult(byType);
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(byType);
        IReadOnlyList<Finding> newFindings = await _newRule.SynthesizeAsync(
            new ObservationMatchSet(observations), new SynthesisContext());

        newFindings.Should().HaveCount(oldFindings.Count);

        for (int i = 0; i < oldFindings.Count; i++)
        {
            InsightFinding oldFinding = oldFindings[i];
            Finding newFinding = newFindings[i];

            newFinding.Title.Should().Be(oldFinding.Title, "finding[{0}].Title", i);
            newFinding.Narrative.Should().Be(oldFinding.Evidence, "finding[{0}].Narrative/Evidence", i);
            newFinding.Recommendation.Should().Be(oldFinding.Recommendation, "finding[{0}].Recommendation", i);
            newFinding.Severity.ToString().Should().Be(oldFinding.Severity.ToString(), "finding[{0}].Severity", i);
            newFinding.Category.Should().Be(oldFinding.Category, "finding[{0}].Category", i);
            newFinding.Analyzer.Should().Be(oldFinding.Analyzer, "finding[{0}].Analyzer", i);
            newFinding.Tags.Should().Equal(oldFinding.Tags, "finding[{0}].Tags", i);
            newFinding.MetricValue.Should().Be(oldFinding.MetricValue, "finding[{0}].MetricValue", i);
            newFinding.MetricUnit.Should().Be(oldFinding.MetricUnit, "finding[{0}].MetricUnit", i);
        }
    }

    private static IReadOnlyList<Observation> ToObservations(IReadOnlyList<(string TypeName, int Count)> byType)
    {
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapObjects };
        var observations = new List<Observation>();

        foreach ((string typeName, int count) in byType)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "infra.timer",
                Subjects = [new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Exact }],
                When = new TemporalExtent { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } },
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(count * 64, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = new Provenance
                {
                    Artifact = TestArtifact,
                    AnalyzerKey = "Timer Leak Analysis",
                    CapabilitiesUsed = capabilitiesUsed,
                    Fidelity = FidelityLevel.Full,
                },
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static TimerLeakDomainResult BuildResult(IReadOnlyList<(string TypeName, int Count)> byType)
    {
        int total = 0, threading = 0, timers = 0, timerQueue = 0, timerHolder = 0, periodic = 0, other = 0;
        var summaries = new List<TimerObjectTypeSummary>();

        foreach ((string typeName, int count) in byType)
        {
            total += count;
            switch (typeName)
            {
                case "System.Threading.Timer": threading += count; break;
                case "System.Timers.Timer": timers += count; break;
                case "System.Threading.TimerQueueTimer": timerQueue += count; break;
                case "System.Threading.TimerHolder": timerHolder += count; break;
                case "System.Threading.PeriodicTimer": periodic += count; break;
                default: other += count; break;
            }
            summaries.Add(new TimerObjectTypeSummary(typeName, count, (ulong)(count * 64)));
        }

        return new TimerLeakDomainResult(
            TimersFound: total > 0,
            TotalTimers: total,
            LogicalTimerCount: timerQueue,
            ThreadingTimerCount: threading,
            TimersTimerCount: timers,
            TimerQueueTimerCount: timerQueue,
            TimerHolderCount: timerHolder,
            PeriodicTimerCount: periodic,
            OtherTimerCount: other,
            TotalBytes: (ulong)(total * 64),
            ByType: summaries,
            IntervalHistogram: []);
    }
}
