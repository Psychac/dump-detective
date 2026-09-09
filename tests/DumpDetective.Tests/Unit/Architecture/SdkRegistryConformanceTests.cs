using System.Text.Json.Nodes;

using DumpDetective.Sdk.Artifacts;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

/// <summary>
/// Phase 1 migration step 7's registry-conformance half: guards
/// <c>schema/DumpDetective.Schema/capability-registry.json</c> and
/// <c>observation-type-registry.json</c> against drifting from the code they mirror. See
/// docs/refactor/modularity/phase-1-contracts-sdk.md.
/// </summary>
public sealed class SdkRegistryConformanceTests
{
    /// <summary>
    /// The <c>ObservationType</c> values real analyzers emit today, and the measure keys each
    /// carries — hand-maintained rather than reflected out of analyzer source, since there are only
    /// three and <c>ObservationType</c>/measure keys are inline string literals
    /// (src/DumpDetective.Sources.NetTrace/{GcPauseAnalyzer,ContentionAnalyzer,CpuHotspotAnalyzer}.cs),
    /// not constants a test could reference directly. Update this table alongside the registry
    /// when a new analyzer starts emitting <see cref="DumpDetective.Sdk.Observations.Observation"/>s.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> KnownEmittedObservationTypes =
        new Dictionary<string, string[]>
        {
            ["gc.pause"] = ["gc.suspend-reason", "pause.duration", "gc.generation", "heap.size"],
            ["contention.episode"] = ["contention.duration"],
            ["cpu.sample-attribution"] = ["cpu.sample-count"],
        };

    [Fact]
    public void CapabilityRegistry_ShouldMatchCapabilityVocabularyExactly()
    {
        JsonObject registry = LoadJsonObject("capability-registry.json");
        JsonObject capabilities = registry["capabilities"]!.AsObject();

        IReadOnlySet<string> registryKeys = capabilities.Select(kvp => kvp.Key).ToHashSet(StringComparer.Ordinal);

        registryKeys.Should().BeEquivalentTo(
            CapabilityVocabulary.Known,
            "capability-registry.json must mirror CapabilityVocabulary.Known exactly — see its own description field");
    }

    [Fact]
    public void ObservationTypeRegistry_ShouldContainEveryObservationTypeRealAnalyzersEmit()
    {
        JsonObject registry = LoadJsonObject("observation-type-registry.json");
        JsonObject observationTypes = registry["observationTypes"]!.AsObject();

        foreach ((string observationType, string[] expectedMeasureKeys) in KnownEmittedObservationTypes)
        {
            observationTypes.ContainsKey(observationType).Should().BeTrue(
                $"a shipped analyzer emits Observations of type '{observationType}' — see KnownEmittedObservationTypes");

            JsonObject entry = observationTypes[observationType]!.AsObject();
            JsonObject measures = entry["measures"]!.AsObject();

            measures.Select(kvp => kvp.Key).Should().BeEquivalentTo(expectedMeasureKeys,
                $"'{observationType}''s registered measure keys must match what the analyzer actually populates");
        }
    }

    [Fact]
    public void ObservationSchema_ShouldBeWellFormedAndCoverKnownObservationTypeAsFreeForm()
    {
        JsonObject schema = LoadJsonObject("observation.schema.json");

        schema["properties"]!["observationType"]!["type"]!.GetValue<string>().Should().Be("string",
            "ObservationType is deliberately free-form (observation-and-correlation-model.md § 7) — " +
            "the closed list of real values lives in observation-type-registry.json, not as a schema enum");
    }

    private static JsonObject LoadJsonObject(string schemaFileName)
    {
        string repoRoot = FindRepositoryRoot();
        string path = Path.Combine(repoRoot, "schema", "DumpDetective.Schema", schemaFileName);

        File.Exists(path).Should().BeTrue($"expected schema file at '{path}'");

        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            string slnxPath = Path.Combine(current.FullName, "DumpDetective.slnx");
            if (File.Exists(slnxPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing DumpDetective.slnx.");
    }
}
