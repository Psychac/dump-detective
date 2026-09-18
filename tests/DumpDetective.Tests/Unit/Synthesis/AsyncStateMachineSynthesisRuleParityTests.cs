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
/// Field-for-field parity between <see cref="AsyncStateMachineFindingGenerator"/> (old) and
/// <see cref="AsyncStateMachineSynthesisRule"/> (new) — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook.
/// </summary>
public sealed class AsyncStateMachineSynthesisRuleParityTests
{
    private readonly AsyncStateMachineFindingGenerator _oldGenerator = new();
    private readonly AsyncStateMachineSynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");
    private const int FireAndForgetThresholdForTests = 100; // mirrors AsyncStateMachineSynthesisRule's own constant

    // Every count below is deliberately kept on just one side of FireAndForgetThreshold(100) and/or
    // HighCountWarning(1000) -- the old generator's independent fields let its own unit tests set
    // e.g. a single 12,000-instance method with TotalStateMachines left at 0, a combination that
    // cannot occur in real production (both aggregates are exhaustive sums over the same type
    // population there). Reconstructing from observations enforces that real coupling, so these
    // fixtures are built consistent with it instead of fighting it -- see the rule's own remarks
    // and the per-analyzer playbook's LohFragmentation entry for the same principle.

    [Fact]
    public Task TotalStateMachinesAtOrAboveTenThousand() => AssertParity(MakeResult(
        totalStateMachines: 10_090, totalBytes: 0,
        topTypes: GenerateChunkedProfiles(10_090, "MyApp.Worker", "Method")));

    [Fact]
    public Task TotalStateMachinesAtOrAboveOneThousand_BelowTenThousand() => AssertParity(MakeResult(
        totalStateMachines: 2_000, totalBytes: 0,
        topTypes: GenerateChunkedProfiles(2_000, "MyApp.Worker", "Method")));

    [Fact]
    public Task TotalStateMachinesBelowOneThousand() => AssertParity(MakeResult(totalStateMachines: 500));

    /// <summary>
    /// A single method responsible for the whole high-count total realistically also crosses
    /// FireAndForgetThreshold, so both findings fire for both the old generator (given matching
    /// SuspendedMethodMap) and the new rule (reconstructed from the same type population).
    /// </summary>
    [Fact]
    public Task TotalStateMachinesHighCount_WithTopTypePopulated() => AssertParity(MakeResult(
        totalStateMachines: 1_800,
        totalBytes: 900_000,
        topTypes: [MakeProfile("MyApp.Worker+<ProcessAsync>d__7", "ProcessAsync", "MyApp.Worker", count: 1_800, totalBytes: 900_000)],
        suspendedMethods: [new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 1_800, TotalBytes: 900_000)]));

    /// <summary>Same reasoning as above: 12,000 suspended instances of one method is also a
    /// high-count-worthy total in reality, so <c>totalStateMachines</c> is set to match.</summary>
    [Fact]
    public Task FireAndForgetSuspendedCountAtOrAboveTenThousand() => AssertParity(MakeResult(
        totalStateMachines: 12_000,
        totalBytes: 500_000,
        topTypes: [MakeProfile("MyApp.Worker+<ProcessAsync>d__1", "ProcessAsync", "MyApp.Worker", count: 12_000, totalBytes: 500_000)],
        suspendedMethods: [new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 12_000, TotalBytes: 500_000)]));

    [Fact]
    public Task FireAndForgetSuspendedCountAtOrAboveHundred_BelowCritical() => AssertParity(MakeResult(suspendedMethods:
    [
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 150, TotalBytes: 5_000),
    ]));

    [Fact]
    public Task FireAndForgetSuspendedCountBelowHundred() => AssertParity(MakeResult(suspendedMethods:
    [
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "ProcessAsync", SuspendedCount: 20, TotalBytes: 500),
    ]));

    /// <summary>Counts kept small enough that their sum (540) stays under HighCountWarning, so this
    /// isolates the fire-and-forget cap without also crossing into high-count territory.</summary>
    [Fact]
    public Task FireAndForgetExceedsMaxFindingsCap() => AssertParity(MakeResult(suspendedMethods:
    [
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method1", SuspendedCount: 150, TotalBytes: 10_000),
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method2", SuspendedCount: 140, TotalBytes: 9_000),
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method3", SuspendedCount: 130, TotalBytes: 8_000),
        new SuspendedMethodEntry(DeclaringType: "MyApp.Worker", MethodName: "Method4", SuspendedCount: 120, TotalBytes: 7_000),
    ]));

    [Fact]
    public Task CapturedBytesAtOrAboveTwoHundredMb() => AssertParity(MakeResult(topByCapturedSize:
    [
        new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 250_000_000UL, LargeCaptures: ["_dbContext"]),
    ]));

    [Fact]
    public Task CapturedBytesAtOrAboveFiftyMb_BelowCritical() => AssertParity(MakeResult(topByCapturedSize:
    [
        new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 60_000_000UL, LargeCaptures: ["_buffer"]),
    ]));

    [Fact]
    public Task CapturedBytesBelowFiftyMb() => AssertParity(MakeResult(topByCapturedSize:
    [
        new HighCaptureStateMachine(Address: 0x1000, TypeName: "MyApp.Handler<>d__1", TotalCapturedRefBytes: 1_000_000UL, LargeCaptures: []),
    ]));

    [Fact]
    public Task NoAsyncVoid() => AssertParity(MakeResult(topTypes: [MakeProfile("Test<Method>d__1", "Method", "TestClass", count: 5, totalBytes: 100, isAsyncVoid: false)]));

    [Fact]
    public Task AsyncVoidWithLowCount() => AssertParity(MakeResult(topTypes: [MakeProfile("Test<AsyncVoidMethod>d__1", "AsyncVoidMethod", "TestClass", count: 5, totalBytes: 100, isAsyncVoid: true)]));

    [Fact]
    public Task AsyncVoidWithHighCount() => AssertParity(MakeResult(topTypes: [MakeProfile("Test<AsyncVoidMethod>d__1", "AsyncVoidMethod", "TestClass", count: 50, totalBytes: 1000, gen2Count: 10, gen2Fraction: 0.2, isAsyncVoid: true)]));

    [Fact]
    public Task MultipleAsyncVoidMethods() => AssertParity(MakeResult(topTypes:
    [
        MakeProfile("Test<Method1>d__1", "Method1", "TestClass", count: 20, totalBytes: 400, gen2Count: 5, gen2Fraction: 0.25, isAsyncVoid: true),
        MakeProfile("Test<Method2>d__2", "Method2", "TestClass", count: 50, totalBytes: 1000, gen2Count: 10, gen2Fraction: 0.2, isAsyncVoid: true),
    ]));

    private async Task AssertParity(AsyncStateMachineDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(AsyncStateMachineDomainResult result)
    {
        var observations = new List<Observation>();
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes, CapabilityVocabulary.DumpObjectFields, CapabilityVocabulary.HeapObjects };

        // Emitted in the given list order, deliberately never re-sorted here -- matches the real
        // analyzer, which emits inline per type, before its own post-loop sorts run (see the rule's
        // own remarks for why trusting emission order would be wrong).
        foreach (StateMachineTypeProfile profile in result.TopStateMachineTypes)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.async-state-machine",
                Subjects =
                [
                    new TypeRef { CanonicalName = profile.TypeName, Fidelity = MatchFidelity.Low },
                    new MethodRef
                    {
                        DeclaringType = new TypeRef { CanonicalName = profile.DeclaringType, Fidelity = MatchFidelity.Low },
                        Name = profile.OriginatingMethod,
                        NormalizedSignature = string.Empty,
                        Fidelity = MatchFidelity.Low,
                    },
                ],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(profile.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(profile.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["dominant-state"] = new Measure(profile.DominantState, MeasureUnit.Custom, MeasureSemantics.Absolute),
                    ["gen2.count"] = new Measure(profile.Gen2Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["gen2.fraction"] = new Measure(profile.Gen2Fraction, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["is-async-void"] = new Measure(profile.IsAsyncVoid ? 1.0 : 0.0, MeasureUnit.Count, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        // If SuspendedMethodMap/high-count fixtures don't populate TopStateMachineTypes (several
        // don't -- they only set the domain result's independent aggregate fields), synthesize the
        // minimum type.async-state-machine population needed to reproduce those aggregates, the same
        // way the ArrayFindingGenerator parity tests keep TopArrayTypesBySize consistent with its
        // own independent aggregate fields.
        if (result.TopStateMachineTypes.Count == 0)
        {
            if (result.SuspendedMethodMap.Count > 0)
            {
                // SuspendedMethodMap already represents the type population (grouped by method) --
                // deriving from it covers TotalStateMachines too, since real production sums that
                // same population. Adding a *separate* total-count placeholder on top would
                // double-count the same instances under two different synthetic types.
                foreach (SuspendedMethodEntry entry in result.SuspendedMethodMap)
                {
                    observations.Add(SyntheticStateMachine($"{entry.DeclaringType}+<{entry.MethodName}>d__0", entry.MethodName, entry.DeclaringType,
                        entry.SuspendedCount, entry.TotalBytes, capabilitiesUsed));
                }
            }
            else if (result.TotalStateMachines > 0)
            {
                // No method-level population at all -- split the total into chunks strictly below
                // FireAndForgetThreshold, each a distinct synthetic method, so reconstructing the
                // total doesn't also manufacture a fire-and-forget finding. All bytes go in the
                // first chunk -- exact enough since only the sum ever appears in this branch's
                // narrative, never a single chunk's share.
                int remaining = result.TotalStateMachines;
                int chunkIndex = 0;
                const int maxChunkCount = FireAndForgetThresholdForTests - 1;
                while (remaining > 0)
                {
                    int chunkCount = Math.Min(remaining, maxChunkCount);
                    ulong chunkBytes = chunkIndex == 0 ? result.TotalStateMachineBytes : 0;
                    observations.Add(SyntheticStateMachine($"Synthetic.Type{chunkIndex}+<Method{chunkIndex}>d__0", $"Method{chunkIndex}", $"Synthetic.Type{chunkIndex}",
                        chunkCount, chunkBytes, capabilitiesUsed));
                    remaining -= chunkCount;
                    chunkIndex++;
                }
            }
        }

        foreach (HighCaptureStateMachine capture in result.TopByCapturedSize)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.async-captured-closure",
                Subjects = [new TypeRef { CanonicalName = capture.TypeName, Fidelity = MatchFidelity.Low }],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["captured.bytes"] = new Measure(capture.TotalCapturedRefBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        return observations;
    }

    private static Observation SyntheticStateMachine(
        string typeName, string method, string declaringType, int count, ulong totalBytes, HashSet<Capability> capabilitiesUsed) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = "type.async-state-machine",
        Subjects =
        [
            new TypeRef { CanonicalName = typeName, Fidelity = MatchFidelity.Low },
            new MethodRef
            {
                DeclaringType = new TypeRef { CanonicalName = declaringType, Fidelity = MatchFidelity.Low },
                Name = method,
                NormalizedSignature = string.Empty,
                Fidelity = MatchFidelity.Low,
            },
        ],
        When = Point(),
        Measures = new Dictionary<string, Measure>
        {
            ["instance.count"] = new Measure(count, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["shallow.bytes"] = new Measure(totalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["dominant-state"] = new Measure(0, MeasureUnit.Custom, MeasureSemantics.Absolute),
            ["gen2.count"] = new Measure(0, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["gen2.fraction"] = new Measure(0, MeasureUnit.Ratio, MeasureSemantics.Ratio),
            ["is-async-void"] = new Measure(0, MeasureUnit.Count, MeasureSemantics.Absolute),
        },
        Provenance = Provenance(capabilitiesUsed),
        Confidence = 1.0,
    };

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Async State Machine Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static StateMachineTypeProfile MakeProfile(
        string typeName, string originatingMethod, string declaringType, int count, ulong totalBytes,
        long gen2Count = 0, double gen2Fraction = 0.0, bool isAsyncVoid = false) => new(
        TypeName: typeName,
        OriginatingMethod: originatingMethod,
        DeclaringType: declaringType,
        Count: count,
        TotalBytes: totalBytes,
        DominantState: 0,
        StateDistribution: [],
        ReferenceFieldCount: 1,
        Gen2Count: gen2Count,
        Gen2Fraction: gen2Fraction,
        IsAsyncVoid: isAsyncVoid);

    /// <summary>
    /// Splits <paramref name="totalCount"/> across many distinct methods, each strictly below
    /// <see cref="FireAndForgetThresholdForTests"/>, so the total reconstructs correctly for the
    /// high-count branch without also manufacturing a fire-and-forget finding. All chunks equal
    /// size (bar the remainder) and emitted in the same order both sides read, so the old
    /// generator's <c>[0]</c> and the rule's tie-broken-by-first-seen arg-max agree on "top method".
    /// </summary>
    private static List<StateMachineTypeProfile> GenerateChunkedProfiles(int totalCount, string declaringType, string methodPrefix)
    {
        var profiles = new List<StateMachineTypeProfile>();
        int remaining = totalCount;
        int index = 0;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, FireAndForgetThresholdForTests - 1);
            string method = $"{methodPrefix}{index}";
            profiles.Add(MakeProfile($"{declaringType}+<{method}>d__{index}", method, declaringType, chunk, 0));
            remaining -= chunk;
            index++;
        }

        return profiles;
    }

    /// <summary>
    /// Defaults to 0/0, not the golden test's 100/1000 -- those are independent-of-population
    /// values the old generator happens to tolerate (100 sits under every threshold regardless of
    /// what else is set), but this test reconstructs the total by summing observations, so a
    /// nonzero-but-unbacked default here would synthesize a phantom state-machine entry and risk a
    /// spurious finding for fixtures that were never about the high-count branch at all.
    /// </summary>
    private static AsyncStateMachineDomainResult MakeResult(
        int totalStateMachines = 0,
        ulong totalBytes = 0,
        IReadOnlyList<StateMachineTypeProfile>? topTypes = null,
        IReadOnlyList<SuspendedMethodEntry>? suspendedMethods = null,
        IReadOnlyList<HighCaptureStateMachine>? topByCapturedSize = null) => new(
        TotalStateMachines: totalStateMachines,
        TotalStateMachineBytes: totalBytes,
        TopStateMachineTypes: topTypes ?? [],
        TopByCapturedSize: topByCapturedSize ?? [],
        SuspendedMethodMap: suspendedMethods ?? []);
}
