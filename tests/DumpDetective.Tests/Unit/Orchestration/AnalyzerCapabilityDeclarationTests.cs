using System.Reflection;

using DumpDetective.Cli.Execution;
using DumpDetective.Orchestration.Discovery;
using DumpDetective.Orchestration.Session;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

/// <summary>
/// Verifies the real 36-analyzer catalog's capability declarations
/// (docs/refactor/modularity/phase-4-session-orchestration.md migration step 3) — that every
/// catalog analyzer declares real, vocabulary-valid capabilities (not just the
/// <c>[AnalyzerModule]</c> discovery attribute), and that resolving them against a real dump-only
/// session produces the expected shape: nothing skipped (no dump analyzer requires anything a
/// dump-only session lacks), most analyzers degraded (missing an optional trace capability).
/// </summary>
public sealed class AnalyzerCapabilityDeclarationTests
{
    private static IReadOnlyList<IAnalyzer> RealCatalog() => new DefaultAnalyzerFactory().CreateAnalyzers();

    [Fact]
    public void EveryCatalogAnalyzer_DeclaresAtLeastOneRequiredCapability()
    {
        IReadOnlyList<IAnalyzer> analyzers = RealCatalog();

        analyzers.Should().HaveCount(36);

        foreach (IAnalyzer analyzer in analyzers)
        {
            RequiresCapabilityAttribute? attribute = analyzer.GetType().GetCustomAttribute<RequiresCapabilityAttribute>();
            attribute.Should().NotBeNull($"{analyzer.GetType().Name} should declare [RequiresCapability]");
            attribute!.Capabilities.Should().NotBeEmpty($"{analyzer.GetType().Name}'s [RequiresCapability] should not be empty");
        }
    }

    [Fact]
    public void EveryDeclaredCapability_IsAKnownVocabularyMember()
    {
        IReadOnlyList<IAnalyzer> analyzers = RealCatalog();

        foreach (IAnalyzer analyzer in analyzers)
        {
            Type analyzerType = analyzer.GetType();

            IReadOnlyList<string> required =
                analyzerType.GetCustomAttribute<RequiresCapabilityAttribute>()?.Capabilities ?? [];
            foreach (string capability in required)
                CapabilityVocabulary.Known.Should().Contain(capability, $"{analyzerType.Name}'s required '{capability}' should be a real capability");

            foreach (OptionalCapabilityAttribute optional in analyzerType.GetCustomAttributes<OptionalCapabilityAttribute>())
                CapabilityVocabulary.Known.Should().Contain(optional.Capability, $"{analyzerType.Name}'s optional '{optional.Capability}' should be a real capability");
        }
    }

    [Fact]
    public void RealDumpOnlySession_SkipsNoAnalyzer()
    {
        // Every dump analyzer's required capabilities are heap.*/runtime.*/dump.* — all satisfied
        // by a dump-only session's static capability set (SingleDumpArtifactFactory). Nothing can
        // land in the skipped bucket until the dump and trace catalogs are unified (step 5).
        IReadOnlyList<IAnalyzer> analyzers = RealCatalog();
        ArtifactDescriptor dumpArtifact = SingleDumpArtifactFactory.Create("C:/fake/dump.dmp");
        AnalysisSession session = SessionBuilder.Build([dumpArtifact]);

        CapabilityResolution resolution = CapabilityResolver.Resolve(analyzers, session.AvailableCapabilities);

        resolution.Unsatisfied.Should().BeEmpty();
        resolution.Satisfied.Should().HaveCount(36);
    }

    [Fact]
    public void RealDumpOnlySession_DegradesEveryAnalyzerThatDeclaresAnOptionalTraceCapability()
    {
        IReadOnlyList<IAnalyzer> analyzers = RealCatalog();
        ArtifactDescriptor dumpArtifact = SingleDumpArtifactFactory.Create("C:/fake/dump.dmp");
        AnalysisSession session = SessionBuilder.Build([dumpArtifact]);

        CapabilityResolution resolution = CapabilityResolver.Resolve(analyzers, session.AvailableCapabilities);

        int expectedDegradedCount = analyzers.Count(a => a.GetType().GetCustomAttributes<OptionalCapabilityAttribute>().Any());
        resolution.Degraded.Should().HaveCount(expectedDegradedCount);
        resolution.Degraded.Should().OnlyContain(d => d.MissingOptional.Count > 0);
    }
}
