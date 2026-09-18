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
/// Field-for-field parity between <see cref="FinalizableObjectFindingGenerator"/> (old) and
/// <see cref="FinalizableObjectSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
/// <remarks>
/// Unlike the old generator's own unit tests (<c>FinalizableObjectFindingGeneratorTests</c>),
/// which set <c>CriticalFinalizerQueueCount</c>/<c>TopQueueTypesByCount</c>/etc. as independent
/// fields with no backing <c>TopQueueEntriesByRetainedSize</c> population, <see cref="BuildResult"/>
/// here derives every aggregate field from a single list of queue entries (and a single list of
/// finalizable-type profiles) — the same "keep fixtures consistent with real production coupling"
/// principle <c>ArraySynthesisRuleParityTests</c> and <c>AsyncStateMachineSynthesisRuleParityTests</c>
/// use, since the new rule can only reconstruct aggregates by summing per-entry observations.
/// </remarks>
public sealed class FinalizableObjectSynthesisRuleParityTests
{
    private readonly FinalizableObjectFindingGenerator _oldGenerator = new();
    private readonly FinalizableObjectSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task NoSignals_ReturnsEmpty() => AssertParity(BuildResult());

    [Fact]
    public Task Gen2AtWarningThreshold() => AssertParity(BuildResult(
        finalizableTypes: [Type("MyApp.LeakedResource", gen2: 1_000, totalBytes: 500_000)]));

    [Fact]
    public Task Gen2AtCriticalThreshold() => AssertParity(BuildResult(
        finalizableTypes: [Type("MyApp.LeakedResource", gen2: 10_000, totalBytes: 5_000_000)]));

    [Fact]
    public Task Gen2BelowWarningThreshold_NoSignal() => AssertParity(BuildResult(
        finalizableTypes: [Type("MyApp.LeakedResource", gen2: 500, totalBytes: 100_000)]));

    [Fact]
    public Task Gen2PicksHighestType() => AssertParity(BuildResult(finalizableTypes:
    [
        Type("MyApp.Small", gen2: 200, totalBytes: 10_000),
        Type("MyApp.Big", gen2: 1_500, totalBytes: 700_000),
    ]));

    [Fact]
    public Task QueueBacklogAtWarningThreshold_NoSignal() => AssertParity(BuildResult(
        finalizerQueueCountOverride: 1_000));

    [Fact]
    public Task QueueBacklogAboveWarningThreshold() => AssertParity(BuildResult(
        finalizerQueueCountOverride: 1_500,
        queueEntries: [Entry("MyApp.Foo", shallow: 1_000, retained: 4_096)]));

    [Fact]
    public Task QueueBacklogAboveCriticalThreshold() => AssertParity(BuildResult(
        finalizerQueueCountOverride: 15_000,
        queueEntries: [Entry("MyApp.Foo", shallow: 1_000, retained: 4_096)]));

    [Fact]
    public Task QueueRetainedBytesAtWarningThreshold() => AssertParity(BuildResult(queueEntries:
    [
        Entry("MyApp.Foo", shallow: 10_000_000, retained: 10_000_000),
    ]));

    [Fact]
    public Task QueueRetainedBytesAtCriticalThreshold() => AssertParity(BuildResult(queueEntries:
    [
        Entry("MyApp.Foo", shallow: 100_000_000, retained: 100_000_000),
    ]));

    [Fact]
    public Task QueueRetainedBytesPartialEstimate() => AssertParity(BuildResult(queueEntries:
    [
        Entry("MyApp.Foo", shallow: 10_000_000, retained: 10_000_000, retainedIsExact: false),
    ]));

    [Fact]
    public Task QueueRetainedBytesWithUndisposedDisposable() => AssertParity(BuildResult(queueEntries:
    [
        Entry("MyApp.Foo", shallow: 10_000_000, retained: 10_000_000, isDisposable: true, disposedFieldFound: true, disposedFieldValue: false),
    ]));

    [Fact]
    public Task CriticalFinalizerAtWarningThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(500, i => Entry($"Microsoft.Win32.SafeHandles.SafeFileHandle", shallow: 64, retained: 64, isCritical: true))));

    [Fact]
    public Task CriticalFinalizerAtCriticalThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(2_000, i => Entry("System.Net.Sockets.SafeSocketHandle", shallow: 64, retained: 64, isCritical: true))));

    [Fact]
    public Task CriticalFinalizerBelowThreshold_NoSignal() => AssertParity(BuildResult(
        queueEntries: BuildMany(50, i => Entry("Microsoft.Win32.SafeHandles.SafeFileHandle", shallow: 64, retained: 64, isCritical: true))));

    [Fact]
    public Task UndisposedDisposableInQueue() => AssertParity(BuildResult(queueEntries:
    [
        Entry("MyApp.Connection", shallow: 100, retained: 100, isDisposable: true, disposedFieldFound: true, disposedFieldValue: false),
        Entry("MyApp.Connection", shallow: 100, retained: 100, isDisposable: true, disposedFieldFound: true, disposedFieldValue: true),
    ]));

    [Fact]
    public Task DynamicResolverBelowInfoThreshold_NoSignal() => AssertParity(BuildResult(
        queueEntries: BuildMany(9, i => Entry("System.Reflection.Emit.DynamicResolver", shallow: 32, retained: 32))));

    [Fact]
    public Task DynamicResolverAtInfoThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(10, i => Entry("System.Reflection.Emit.DynamicResolver", shallow: 32, retained: 32))));

    [Fact]
    public Task DynamicResolverAtWarningThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(100, i => Entry("System.Reflection.Emit.DynamicResolver", shallow: 32, retained: 32))));

    [Fact]
    public Task AbandonedThreadsAtThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(50, i => Entry("System.Threading.Thread", shallow: 512, retained: 512))));

    [Fact]
    public Task UndisposedTimersAtInfoThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(5, i => Entry("System.Threading.TimerHolder", shallow: 64, retained: 64))));

    [Fact]
    public Task AbandonedReaderWriterLocksAtThreshold() => AssertParity(BuildResult(
        queueEntries: BuildMany(3, i => Entry("System.Threading.ReaderWriterLock", shallow: 64, retained: 64))));

    [Fact]
    public Task NoKnownPatternTypesInQueue_NoKnownPatternFindings() => AssertParity(BuildResult(
        queueEntries: BuildMany(1_000, i => Entry("MyApp.SomeUnrelatedType", shallow: 64, retained: 64))));

    [Fact]
    public Task AllSignalsFireTogether() => AssertParity(BuildResult(
        finalizableTypes: [Type("MyApp.LeakedResource", gen2: 10_500, totalBytes: 5_000_000)],
        finalizerQueueCountOverride: 15_100,
        queueEntries:
        [
            Entry("Microsoft.Win32.SafeHandles.SafeFileHandle", shallow: 64, retained: 64, isCritical: true),
            .. BuildMany(2_000, _ => Entry("System.Net.Sockets.SafeSocketHandle", shallow: 64, retained: 64, isCritical: true)),
            .. BuildMany(100, _ => Entry("System.Reflection.Emit.DynamicResolver", shallow: 32, retained: 32)),
            .. BuildMany(50, _ => Entry("System.Threading.Thread", shallow: 512, retained: 512)),
            .. BuildMany(50, _ => Entry("System.Threading.TimerHolder", shallow: 64, retained: 64)),
            .. BuildMany(3, _ => Entry("System.Threading.ReaderWriterLock", shallow: 64, retained: 64)),
            Entry("MyApp.Connection", shallow: 100_000_000, retained: 100_000_000, isDisposable: true, disposedFieldFound: true, disposedFieldValue: false),
        ]));

    private static IReadOnlyList<FinalizerQueueEntry> BuildMany(int count, Func<int, FinalizerQueueEntry> factory)
    {
        var list = new List<FinalizerQueueEntry>(count);
        for (int i = 0; i < count; i++) list.Add(factory(i));
        return list;
    }

    private static TypeGenerationProfile Type(string typeName, long gen2, ulong totalBytes) => new(
        TypeName: typeName,
        Gen0Count: 0,
        Gen1Count: 0,
        Gen2Count: gen2,
        LohCount: 0,
        TotalBytes: totalBytes,
        Gen2Bytes: totalBytes,
        IsFinalizable: true);

    private static FinalizerQueueEntry Entry(
        string typeName,
        ulong shallow,
        ulong retained,
        bool isDisposable = false,
        bool disposedFieldFound = false,
        bool disposedFieldValue = false,
        bool isCritical = false,
        bool retainedIsExact = true) => new(
        Address: 0x1000,
        TypeName: typeName,
        ShallowSize: shallow,
        EstimatedRetainedBytes: retained,
        IsDisposableType: isDisposable,
        DisposedFieldFound: disposedFieldFound,
        DisposedFieldValue: disposedFieldValue,
        RetainedBytesIsExact: retainedIsExact,
        IsCriticalFinalizer: isCritical);

    private async Task AssertParity(FinalizableObjectDomainResult result)
    {
        IReadOnlyList<InsightFinding> oldFindings = _oldGenerator.Generate(result);
        IReadOnlyList<Observation> observations = ToObservations(result);
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

    private static IReadOnlyList<Observation> ToObservations(FinalizableObjectDomainResult result)
    {
        var observations = new List<Observation>();
        var populationCapabilities = new HashSet<Capability> { CapabilityVocabulary.HeapTypes };
        var queueCapabilities = new HashSet<Capability>
        {
            CapabilityVocabulary.HeapFinalizerQueue, CapabilityVocabulary.HeapSegments, CapabilityVocabulary.HeapTypes,
        };

        foreach (TypeGenerationProfile profile in result.TopFinalizableTypesByGen2Count)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.finalizable-population",
                Subjects = [new TypeRef { CanonicalName = profile.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(profile.Gen0Count + profile.Gen1Count + profile.Gen2Count + profile.LohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(profile.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(profile.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(populationCapabilities),
                Confidence = 1.0,
            });
        }

        foreach (FinalizerQueueEntry entry in result.TopQueueEntriesByRetainedSize)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "finalizer.queue-entry",
                Subjects = [new TypeRef { CanonicalName = entry.TypeName, Fidelity = MatchFidelity.Exact }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["retained.bytes"] = new Measure(entry.EstimatedRetainedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(entry.ShallowSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["disposed-but-live"] = new Measure(entry.IsDisposableType && entry.DisposedFieldFound && !entry.DisposedFieldValue ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["is-critical-finalizer"] = new Measure(entry.IsCriticalFinalizer ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(queueCapabilities),
                Confidence = entry.RetainedBytesIsExact ? 1.0 : 0.7,
            });
        }

        observations.Add(new Observation
        {
            Id = ObservationId.NewId(),
            ObservationType = "finalizer.queue-population",
            Subjects = [],
            When = Point(),
            Measures = new Dictionary<string, Measure>
            {
                ["queue.count"] = new Measure(result.FinalizerQueueCount, MeasureUnit.Count, MeasureSemantics.Absolute),
            },
            Provenance = Provenance(new HashSet<Capability> { CapabilityVocabulary.HeapFinalizerQueue }),
            Confidence = 1.0,
        });

        return observations;
    }

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Finalizable Object Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static FinalizableObjectDomainResult BuildResult(
        IReadOnlyList<TypeGenerationProfile>? finalizableTypes = null,
        IReadOnlyList<FinalizerQueueEntry>? queueEntries = null,
        int? finalizerQueueCountOverride = null)
    {
        finalizableTypes ??= [];
        queueEntries ??= [];

        long totalObjects = 0;
        ulong totalBytes = 0;
        long gen0 = 0, gen1 = 0, gen2 = 0, loh = 0;
        foreach (TypeGenerationProfile t in finalizableTypes)
        {
            totalObjects += t.Gen0Count + t.Gen1Count + t.Gen2Count + t.LohCount;
            totalBytes += t.TotalBytes;
            gen0 += t.Gen0Count;
            gen1 += t.Gen1Count;
            gen2 += t.Gen2Count;
            loh += t.LohCount;
        }

        var sortedFinalizableTypes = finalizableTypes.OrderByDescending(t => t.Gen2Count).ToList();

        ulong retainedBytes = 0;
        bool isRetainedEstimatePartial = false;
        bool hasUndisposedDisposable = false;
        int criticalCount = 0;
        ulong criticalBytes = 0;
        var queueTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var criticalTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (FinalizerQueueEntry e in queueEntries)
        {
            retainedBytes += e.EstimatedRetainedBytes;
            if (!e.RetainedBytesIsExact) isRetainedEstimatePartial = true;
            if (e.IsDisposableType && e.DisposedFieldFound && !e.DisposedFieldValue) hasUndisposedDisposable = true;

            queueTypeCounts[e.TypeName] = queueTypeCounts.GetValueOrDefault(e.TypeName) + 1;

            if (e.IsCriticalFinalizer)
            {
                criticalCount++;
                criticalBytes += e.ShallowSize;
                criticalTypeCounts[e.TypeName] = criticalTypeCounts.GetValueOrDefault(e.TypeName) + 1;
            }
        }

        var topQueueTypesByCount = queueTypeCounts
            .Select(kv => new QueueTypeStatistic(kv.Key, kv.Value))
            .OrderByDescending(s => s.QueueCount)
            .ToList();
        var topCriticalFinalizerTypesByCount = criticalTypeCounts
            .Select(kv => new QueueTypeStatistic(kv.Key, kv.Value))
            .OrderByDescending(s => s.QueueCount)
            .ToList();

        return new FinalizableObjectDomainResult(
            TotalFinalizableObjects: (int)totalObjects,
            TotalFinalizableBytes: totalBytes,
            Gen0Count: gen0,
            Gen1Count: gen1,
            Gen2Count: gen2,
            LohCount: loh,
            FinalizerQueueCount: finalizerQueueCountOverride ?? queueEntries.Count,
            FinalizerQueueRetainedBytes: retainedBytes,
            IsRetainedEstimatePartial: isRetainedEstimatePartial,
            HasUndisposedDisposableInQueue: hasUndisposedDisposable,
            CriticalFinalizerQueueCount: criticalCount,
            CriticalFinalizerQueueBytes: criticalBytes,
            TopFinalizableTypesByGen2Count: sortedFinalizableTypes,
            TopQueueTypesByCount: topQueueTypesByCount,
            TopCriticalFinalizerTypesByCount: topCriticalFinalizerTypesByCount,
            TopQueueEntriesByRetainedSize: queueEntries);
    }
}
