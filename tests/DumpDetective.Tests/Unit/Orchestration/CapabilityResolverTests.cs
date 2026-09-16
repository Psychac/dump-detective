using DumpDetective.Orchestration.Discovery;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

public sealed class CapabilityResolverTests
{
    [Fact]
    public void Resolve_KeepsAnalyzer_WhenNoCapabilitiesRequired()
    {
        var resolution = CapabilityResolver.Resolve(
            [new UnconstrainedAnalyzer()],
            availableCapabilities: new HashSet<Capability>());

        resolution.Satisfied.Should().ContainSingle();
        resolution.Unsatisfied.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_KeepsAnalyzer_WhenAllRequiredCapabilitiesAvailable()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability>
        {
            new(CapabilityVocabulary.HeapObjects),
            new(CapabilityVocabulary.HeapTypes),
        };

        var resolution = CapabilityResolver.Resolve([new RequiresHeapObjectsAndTypesAnalyzer()], available);

        resolution.Satisfied.Should().ContainSingle();
        resolution.Unsatisfied.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_DropsAnalyzer_WhenARequiredCapabilityIsMissing()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability> { new(CapabilityVocabulary.HeapObjects) };

        var resolution = CapabilityResolver.Resolve([new RequiresHeapObjectsAndTypesAnalyzer()], available);

        resolution.Satisfied.Should().BeEmpty();
        resolution.Unsatisfied.Should().ContainSingle();
        resolution.Unsatisfied[0].MissingCapabilities.Should().ContainSingle(CapabilityVocabulary.HeapTypes);
    }

    [Fact]
    public void Resolve_PartitionsIndependently_AcrossMultipleCandidates()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability> { new(CapabilityVocabulary.HeapObjects) };

        var resolution = CapabilityResolver.Resolve(
            [new UnconstrainedAnalyzer(), new RequiresHeapObjectsAndTypesAnalyzer()],
            available);

        resolution.Satisfied.Should().ContainSingle().Which.Should().BeOfType<UnconstrainedAnalyzer>();
        resolution.Unsatisfied.Should().ContainSingle();
    }

    [Fact]
    public void Resolve_MarksAnalyzerDegraded_WhenRequiredMetButOptionalMissing()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability> { new(CapabilityVocabulary.HeapObjects) };

        var resolution = CapabilityResolver.Resolve([new OptionalTraceGcEventsAnalyzer()], available);

        resolution.Satisfied.Should().ContainSingle();
        resolution.Unsatisfied.Should().BeEmpty();
        resolution.Degraded.Should().ContainSingle();
        resolution.Degraded[0].MissingOptional.Should().ContainSingle(
            m => m.Capability == CapabilityVocabulary.TraceGcEvents && m.Fidelity == FidelityBoost.Major);
    }

    [Fact]
    public void Resolve_DoesNotMarkAnalyzerDegraded_WhenOptionalCapabilityAvailable()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability>
        {
            new(CapabilityVocabulary.HeapObjects),
            new(CapabilityVocabulary.TraceGcEvents),
        };

        var resolution = CapabilityResolver.Resolve([new OptionalTraceGcEventsAnalyzer()], available);

        resolution.Satisfied.Should().ContainSingle();
        resolution.Degraded.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_DoesNotMarkUnsatisfiedAnalyzerDegraded_EvenWithMissingOptionalCapability()
    {
        IReadOnlySet<Capability> available = new HashSet<Capability>();

        var resolution = CapabilityResolver.Resolve([new OptionalTraceGcEventsAnalyzer()], available);

        resolution.Satisfied.Should().BeEmpty();
        resolution.Unsatisfied.Should().ContainSingle();
        resolution.Degraded.Should().BeEmpty();
    }

    private sealed class UnconstrainedAnalyzer : IAnalyzer
    {
        public string Name => nameof(UnconstrainedAnalyzer);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [RequiresCapability(CapabilityVocabulary.HeapObjects, CapabilityVocabulary.HeapTypes)]
    private sealed class RequiresHeapObjectsAndTypesAnalyzer : IAnalyzer
    {
        public string Name => nameof(RequiresHeapObjectsAndTypesAnalyzer);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [RequiresCapability(CapabilityVocabulary.HeapObjects)]
    [OptionalCapability(CapabilityVocabulary.TraceGcEvents, Fidelity = FidelityBoost.Major)]
    private sealed class OptionalTraceGcEventsAnalyzer : IAnalyzer
    {
        public string Name => nameof(OptionalTraceGcEventsAnalyzer);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
