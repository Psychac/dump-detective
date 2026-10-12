using DumpDetective.Orchestration.Discovery;
using DumpDetective.Orchestration.Session;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Temporal;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

public sealed class CoverageRecordBuilderTests
{
    [Fact]
    public void Build_MarksEveryProvidedCapabilityCompleteForItsArtifact()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapTypes);
        AnalysisSession session = new([dump], dump.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(session, CapabilityResolver.Resolve([], session.AvailableCapabilities));

        records.Should().HaveCount(2);
        records.Should().OnlyContain(r => r.Status == CoverageStatus.Complete && r.Artifact == dump.Id && r.Impacts.Count == 0);
    }

    [Fact]
    public void Build_RecordsMissingRequiredCapabilityAsUnavailableWithNoArtifact_NamingTheAnalyzerThatDidNotRun()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects);
        AnalysisSession session = new([dump], dump.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new NeedsTraceGcEvents()], session.AvailableCapabilities));

        CoverageRecord missing = records.Single(r => r.Capability == CapabilityVocabulary.TraceGcEvents);
        missing.Status.Should().Be(CoverageStatus.NotSupplied);
        missing.Artifact.Should().BeNull();
        missing.Impacts.Should().ContainSingle().Which.Should().Be(new CoverageImpact(nameof(NeedsTraceGcEvents), CoverageImpactKind.NotRun));
    }

    [Fact]
    public void Build_RecordsMissingOptionalCapabilityWithItsFidelityBoost()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects);
        AnalysisSession session = new([dump], dump.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new PrefersTraceGcEvents()], session.AvailableCapabilities));

        records.Single(r => r.Capability == CapabilityVocabulary.TraceGcEvents).Impacts.Should().ContainSingle()
            .Which.Should().Be(new CoverageImpact(nameof(PrefersTraceGcEvents), CoverageImpactKind.ReducedFidelity, FidelityBoost.Major));
    }

    [Fact]
    public void Build_TreatsADumpAndATraceAlike_EachCompleteForItsOwnArtifact()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects);
        ArtifactDescriptor trace = Artifact("trace-1", "nettrace", CapabilityVocabulary.TraceGcEvents);
        AnalysisSession session = new([dump, trace], new HashSet<Capability>(dump.Provides.Concat(trace.Provides)));

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new NeedsTraceGcEvents()], session.AvailableCapabilities));

        records.Should().HaveCount(2);
        records.Single(r => r.Capability == CapabilityVocabulary.TraceGcEvents).Artifact.Should().Be(trace.Id);
        records.Should().OnlyContain(r => r.Status == CoverageStatus.Complete);
    }

    [Fact]
    public void Build_AggregatesAllAffectedAnalyzersUnderOneRecordPerMissingCapability()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects);
        AnalysisSession session = new([dump], dump.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new NeedsTraceGcEvents(), new PrefersTraceGcEvents()], session.AvailableCapabilities));

        records.Where(r => r.Capability == CapabilityVocabulary.TraceGcEvents).Should().ContainSingle()
            .Which.Impacts.Should().HaveCount(2);
    }

    [Fact]
    public void Build_MarksAMissingCapabilityUnavailable_WhenAnArtifactOfItsFamilyIsPresent()
    {
        ArtifactDescriptor dump = Artifact("dump-1", "clr-dump", CapabilityVocabulary.HeapObjects);
        AnalysisSession session = new([dump], dump.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new NeedsHeapTypes()], session.AvailableCapabilities));

        records.Single(r => r.Capability == CapabilityVocabulary.HeapTypes).Status.Should().Be(CoverageStatus.Unavailable);
    }

    [Fact]
    public void Build_MarksAMissingTraceCapabilityUnavailable_WhenATraceIsPresentButLacksIt()
    {
        ArtifactDescriptor trace = Artifact("trace-1", "nettrace", CapabilityVocabulary.TraceGcEvents);
        AnalysisSession session = new([trace], trace.Provides);

        IReadOnlyList<CoverageRecord> records = CoverageRecordBuilder.Build(
            session, CapabilityResolver.Resolve([new NeedsTraceCpuSamples()], session.AvailableCapabilities));

        records.Single(r => r.Capability == CapabilityVocabulary.TraceCpuSamples).Status.Should().Be(CoverageStatus.Unavailable);
    }

    private static ArtifactDescriptor Artifact(string id, string sourceKind, params string[] provides) => new()
    {
        Id = new ArtifactId(id),
        SourceKind = sourceKind,
        Path = id,
        CapturedAt = new TimeAnchor { WallClockUtc = DateTime.UnixEpoch, Confidence = AnchorConfidence.Approximate },
        Process = new ProcessIdentity { ProcessId = 1, ImageName = "test" },
        Provides = provides.Select(key => new Capability(key)).ToHashSet(),
    };

    [RequiresCapability(CapabilityVocabulary.TraceGcEvents)]
    private sealed class NeedsTraceGcEvents : IAnalyzer
    {
        public string Name => nameof(NeedsTraceGcEvents);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [RequiresCapability(CapabilityVocabulary.HeapObjects)]
    [OptionalCapability(CapabilityVocabulary.TraceGcEvents, Fidelity = FidelityBoost.Major)]
    private sealed class PrefersTraceGcEvents : IAnalyzer
    {
        public string Name => nameof(PrefersTraceGcEvents);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [RequiresCapability(CapabilityVocabulary.HeapTypes)]
    private sealed class NeedsHeapTypes : IAnalyzer
    {
        public string Name => nameof(NeedsHeapTypes);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [RequiresCapability(CapabilityVocabulary.TraceCpuSamples)]
    private sealed class NeedsTraceCpuSamples : IAnalyzer
    {
        public string Name => nameof(NeedsTraceCpuSamples);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
