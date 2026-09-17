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
/// Field-for-field parity between <see cref="ArrayFindingGenerator"/> (old) and
/// <see cref="ArraySynthesisRule"/> (new) over the same fixtures used by
/// <c>Golden/ArrayFindingGeneratorGoldenTests.cs</c>. This is the "byte-identical" bar from
/// docs/refactor/modularity/phase-5-observations-synthesis.md's per-analyzer playbook, checked on
/// the reader-facing fields <see cref="Finding"/> and <see cref="InsightFinding"/> both carry —
/// not a JSON-shape comparison, since the two record types are deliberately different shapes.
/// </summary>
/// <remarks>
/// Unlike the golden tests, fixtures here also populate <c>TopArrayTypesBySize</c> consistently
/// with the aggregate fields (<c>LohArrayBytes</c>/<c>MultiDimArrayCount</c>/etc.) — the real
/// <c>ArrayAnalyzer</c> always keeps these consistent since both come from the same scan, but the
/// golden tests' fixtures (written against the old generator's independent-fields contract) don't
/// bother. <see cref="ArraySynthesisRule"/> reconstructs the aggregates by summing
/// <c>type.array-composition</c> observations, so a fixture with an empty
/// <c>TopArrayTypesBySize</c> would make it see zero even where the old generator sees a real
/// aggregate — this class's own <c>BuildResult</c> keeps them in sync instead.
/// </remarks>
public sealed class ArraySynthesisRuleParityTests
{
    private readonly ArrayFindingGenerator _oldGenerator = new();
    private readonly ArraySynthesisRule _newRule = new();
    private static readonly ArtifactId TestArtifact = new("test-artifact");

    [Fact]
    public Task LohArrayBytesAtOrAboveTwoGb() =>
        AssertParity(BuildResult(lohArrayBytes: 2_000_000_000UL, lohArrayCount: 5));

    [Fact]
    public Task LohArrayBytesBetweenWarningAndCritical() =>
        AssertParity(BuildResult(lohArrayBytes: 600_000_000UL, lohArrayCount: 3));

    [Fact]
    public Task LohArrayBytesBelowWarning() =>
        AssertParity(BuildResult(lohArrayBytes: 100_000_000UL, lohArrayCount: 1));

    [Fact]
    public Task LohArrayWithTopLargeArrayPopulated() =>
        AssertParity(BuildResult(
            lohArrayBytes: 2_000_000_000UL,
            lohArrayCount: 5,
            topLargeArrays: [new LargeArrayEntry(Address: 0x2000, ElementTypeName: "System.Byte", Length: 250_000_000, Rank: 1, Size: 2_000_000_000UL)]));

    [Fact]
    public Task MultiDimArrayCountAtOrAboveThreshold() =>
        AssertParity(BuildResult(multiDimArrayCount: 1_500));

    [Fact]
    public Task MultiDimArrayBytesAtOrAboveThreshold_BelowCountThreshold() =>
        AssertParity(BuildResult(multiDimArrayCount: 10, multiDimArrayBytes: 150_000_000UL));

    [Fact]
    public Task MultiDimArrayBelowBothThresholds() =>
        AssertParity(BuildResult(multiDimArrayCount: 10, multiDimArrayBytes: 1_000_000UL));

    [Fact]
    public Task SparseArrayAboveThresholds() =>
        AssertParity(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 20_000_000UL),
        ]));

    [Fact]
    public Task SparseArrayBelowRatioThreshold() =>
        AssertParity(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 500, SparseRatio: 0.5, WastedBytes: 20_000_000UL),
        ]));

    [Fact]
    public Task SparseArrayExceedsMaxFindingsCap() =>
        AssertParity(BuildResult(topSparseArrays:
        [
            new SparseArrayEntry(Address: 0x1000, ElementTypeName: "System.Int32", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 20_000_000UL),
            new SparseArrayEntry(Address: 0x2000, ElementTypeName: "System.Int64", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 21_000_000UL),
            new SparseArrayEntry(Address: 0x3000, ElementTypeName: "System.Double", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 22_000_000UL),
            new SparseArrayEntry(Address: 0x4000, ElementTypeName: "System.String", Length: 1000, NullOrZeroCount: 900, SparseRatio: 0.9, WastedBytes: 23_000_000UL),
        ]));

    [Fact]
    public Task PinnedArrayBytesAtOrAboveCritical() =>
        AssertParity(BuildResult(pinnedArrayCount: 150, pinnedArrayBytes: 200_000_000UL));

    [Fact]
    public Task PinnedArrayCountAboveThreshold_BelowCriticalBytes() =>
        AssertParity(BuildResult(pinnedArrayCount: 150, pinnedArrayBytes: 1_000_000UL));

    [Fact]
    public Task PinnedArrayWithTopPinnedArrayPopulated() =>
        AssertParity(BuildResult(
            pinnedArrayCount: 150,
            pinnedArrayBytes: 200_000_000UL,
            topPinnedArrays: [new LargeArrayEntry(Address: 0x3000, ElementTypeName: "System.Byte", Length: 50_000_000, Rank: 1, Size: 200_000_000UL)]));

    [Fact]
    public Task AllBelowThresholds() =>
        AssertParity(BuildResult());

    private async Task AssertParity(ArrayDomainResult result)
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

    private static IReadOnlyList<Observation> ToObservations(ArrayDomainResult result)
    {
        var observations = new List<Observation>();
        var capabilitiesUsed = new HashSet<Capability> { CapabilityVocabulary.HeapTypes, CapabilityVocabulary.HeapObjects };

        foreach (ArrayTypeProfile profile in result.TopArrayTypesBySize)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "type.array-composition",
                Subjects = [Type(profile.ElementTypeName)],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["instance.count"] = new Measure(profile.Count, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["shallow.bytes"] = new Measure(profile.TotalBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["gen2-plus-loh.percent"] = new Measure(profile.Gen2PlusLohPercent, MeasureUnit.Percent, MeasureSemantics.Ratio),
                    ["avg-size.bytes"] = new Measure(profile.AverageInstanceSize, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["array.rank"] = new Measure(profile.Rank, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.count"] = new Measure(profile.LohCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["loh.bytes"] = new Measure(profile.LohBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                    ["pinned.count"] = new Measure(profile.PinnedCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["pinned.bytes"] = new Measure(profile.PinnedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
            });
        }

        foreach (SparseArrayEntry sparse in result.TopSparseArrays)
        {
            observations.Add(new Observation
            {
                Id = ObservationId.NewId(),
                ObservationType = "heap.sparse-array",
                Subjects = [Type(sparse.ElementTypeName)],
                When = Point(),
                Measures = new Dictionary<string, Measure>
                {
                    ["length"] = new Measure(sparse.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["null-or-zero.count"] = new Measure(sparse.NullOrZeroCount, MeasureUnit.Count, MeasureSemantics.Absolute),
                    ["sparse.ratio"] = new Measure(sparse.SparseRatio, MeasureUnit.Ratio, MeasureSemantics.Ratio),
                    ["wasted.bytes"] = new Measure(sparse.WastedBytes, MeasureUnit.Bytes, MeasureSemantics.Absolute),
                },
                Provenance = Provenance(capabilitiesUsed),
                Confidence = 1.0,
                Evidence = [new EvidenceRef { Artifact = TestArtifact, Locator = sparse.Address.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            });
        }

        foreach (LargeArrayEntry large in result.TopLargeArrays)
            observations.Add(LargeArrayObservation("heap.large-array", large, capabilitiesUsed));

        foreach (LargeArrayEntry pinned in result.TopPinnedArrays ?? [])
            observations.Add(LargeArrayObservation("heap.pinned-array", pinned, capabilitiesUsed));

        return observations;
    }

    private static Observation LargeArrayObservation(string observationType, LargeArrayEntry entry, HashSet<Capability> capabilitiesUsed) => new()
    {
        Id = ObservationId.NewId(),
        ObservationType = observationType,
        Subjects = [Type(entry.ElementTypeName)],
        When = Point(),
        Measures = new Dictionary<string, Measure>
        {
            ["length"] = new Measure(entry.Length, MeasureUnit.Count, MeasureSemantics.Absolute),
            ["shallow.bytes"] = new Measure(entry.Size, MeasureUnit.Bytes, MeasureSemantics.Absolute),
            ["array.rank"] = new Measure(entry.Rank, MeasureUnit.Count, MeasureSemantics.Absolute),
        },
        Provenance = Provenance(capabilitiesUsed),
        Confidence = 1.0,
    };

    private static TypeRef Type(string canonicalName) => new() { CanonicalName = canonicalName, Fidelity = MatchFidelity.Exact };

    private static TemporalExtent Point() => new() { Kind = TemporalKind.Point, Start = new TimeAnchor { Confidence = AnchorConfidence.Unknown } };

    private static Provenance Provenance(HashSet<Capability> capabilitiesUsed) => new()
    {
        Artifact = TestArtifact,
        AnalyzerKey = "Array Analysis",
        CapabilitiesUsed = capabilitiesUsed,
        Fidelity = FidelityLevel.Full,
    };

    private static ArrayDomainResult BuildResult(
        ulong lohArrayBytes = 0,
        int lohArrayCount = 0,
        int multiDimArrayCount = 0,
        ulong multiDimArrayBytes = 0,
        IReadOnlyList<LargeArrayEntry>? topLargeArrays = null,
        IReadOnlyList<SparseArrayEntry>? topSparseArrays = null,
        int pinnedArrayCount = 0,
        ulong pinnedArrayBytes = 0,
        IReadOnlyList<LargeArrayEntry>? topPinnedArrays = null)
    {
        var topArrayTypes = new List<ArrayTypeProfile>();
        if (lohArrayCount > 0 || multiDimArrayCount > 0 || pinnedArrayCount > 0)
        {
            int rank = multiDimArrayCount > 0 ? 2 : 1;
            int count = Math.Max(multiDimArrayCount, Math.Max(lohArrayCount, pinnedArrayCount));
            ulong bytes = Math.Max(multiDimArrayBytes, Math.Max(lohArrayBytes, pinnedArrayBytes));
            topArrayTypes.Add(new ArrayTypeProfile(
                ElementTypeName: "System.Byte",
                Rank: rank,
                Count: count,
                TotalBytes: bytes,
                IsMultiDimensional: rank >= 2,
                LohCount: lohArrayCount,
                LohBytes: lohArrayBytes,
                PinnedCount: pinnedArrayCount,
                PinnedBytes: pinnedArrayBytes));
        }

        return new ArrayDomainResult(
            TotalArrayObjects: 10_000,
            TotalArrayBytes: 1_000_000_000,
            MultiDimArrayCount: multiDimArrayCount,
            MultiDimArrayBytes: multiDimArrayBytes,
            LohArrayCount: lohArrayCount,
            LohArrayBytes: lohArrayBytes,
            TopArrayTypesBySize: topArrayTypes,
            TopLargeArrays: topLargeArrays ?? [],
            TopSparseArrays: topSparseArrays ?? [],
            PinnedArrayCount: pinnedArrayCount,
            PinnedArrayBytes: pinnedArrayBytes,
            TopPinnedArrays: topPinnedArrays);
    }
}
