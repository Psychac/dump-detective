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
    /// carries — hand-maintained rather than reflected out of analyzer source, since
    /// <c>ObservationType</c>/measure keys are inline string literals, not constants a test could
    /// reference directly. Update this table alongside the registry when a new analyzer starts
    /// emitting <see cref="DumpDetective.Sdk.Observations.Observation"/>s.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> KnownEmittedObservationTypes =
        new Dictionary<string, string[]>
        {
            ["gc.pause"] = ["gc.suspend-reason", "pause.duration", "gc.generation", "heap.size"],
            ["contention.episode"] = ["contention.duration"],
            ["cpu.sample-attribution"] = ["cpu.sample-count"],
            ["heap.root"] = ["retained.bytes", "subgraph.node-count"],
            ["handle.pinned"] = ["retained.bytes"],
            ["finalizer.queue-entry"] = ["retained.bytes", "disposed-but-live"],
            ["weakhandle.target"] = ["retained.bytes"],
            ["weakreference.stale-wrapper"] = ["shallow.bytes"],
            ["gc.generation-composition"] = ["gen0.bytes", "gen1.bytes", "gen2.bytes", "loh.bytes", "poh.bytes", "gen2.fraction", "loh.fraction"],
            ["gc.allocation-composition"] = ["gen0.count-fraction", "gen1.count-fraction", "gen2.count-fraction", "loh.count-fraction", "finalizable.type-count", "finalizable.bytes"],
            ["heap.segment-composition"] = ["generation.ordinal", "committed.bytes", "reserved.bytes", "used.bytes", "segment.count"],
            ["heap.loh-fragmentation"] = ["kind.ordinal", "segment.count", "total.bytes", "free.bytes", "used.bytes", "fragmentation.percent", "largest-free-block.bytes"],
            ["heap.loh-type-composition"] = ["loh.count", "loh.bytes"],
            ["heap.segment-reservation"] = ["committed.bytes", "reserved.bytes", "reservation-gap.bytes", "reserved-to-committed.ratio", "ephemeral.segment-count", "ephemeral.avg-fill-percent", "segment.count"],
            ["heap.region-composition"] = ["region-kind.ordinal", "region.count", "reserved.bytes", "committed.bytes", "near-empty.count", "near-empty.committed-bytes"],
            ["thread.blocked"] = ["locks-held.count"],
            ["lock.contested"] = ["waiting.count", "recursion.count"],
            ["thread.wait-state"] = ["locks-held.count"],
            ["thread.state"] = ["stack-size.bytes", "locks-held.count", "stack-root.count", "has-exception", "async-chain.depth"],
            ["thread.stack-cluster"] = ["cluster.count", "threadpool-worker.count", "gc.count", "finalizer.count"],
            ["infra.sql-connection-pool"] = ["current.size", "max.size", "min.size"],
            ["infra.sql-command"] = ["shallow.bytes"],
            ["infra.sql-transaction"] = ["shallow.bytes"],
            ["infra.db-connection"] = ["generation.ordinal", "retained.bytes"],
            ["infra.http-object"] = ["shallow.bytes"],
            ["infra.timer"] = ["instance.count", "shallow.bytes"],
            ["infra.wcf-channel"] = ["retained.bytes"],
            ["runtime.jit-activity"] = ["jit-heap.bytes", "active-methods.count", "distinct-methods.count", "tiered-methods.count", "managed-frame.count", "unmanaged-frame.count", "ready-to-run-frame.count", "dynamic-method-frame.count"],
            ["heap.segment-kind-composition"] = ["kind.ordinal", "segment.count", "object.count", "committed.bytes", "reserved.bytes"],
            ["heap.poh-type-composition"] = ["instance.count", "shallow.bytes"],
            ["heap.frozen-type-composition"] = ["instance.count", "shallow.bytes"],
            ["runtime.module-heap-footprint"] = ["unique-type.count", "object.count", "shallow.bytes", "loh.bytes", "gen2.object-count"],
            ["runtime.active-exception"] = ["generation.ordinal", "shallow.bytes"],
            ["type.shape-composition"] = ["type.count", "avg-reference-fields", "gc-scan-work", "gen2-gc-scan-work"],
            ["type.boxing"] = ["instance.count", "shallow.bytes", "gen0.count", "gen2.count", "gen2.fraction", "is-enum", "has-reference-fields", "has-iequatable"],
            ["type.array-composition"] = ["instance.count", "shallow.bytes", "gen2-plus-loh.percent", "avg-size.bytes", "array.rank", "loh.count", "loh.bytes", "pinned.count", "pinned.bytes"],
            ["heap.sparse-array"] = ["length", "null-or-zero.count", "sparse.ratio", "wasted.bytes"],
            ["heap.large-array"] = ["length", "shallow.bytes", "array.rank"],
            ["heap.pinned-array"] = ["length", "shallow.bytes", "array.rank"],
            ["collection.wasteful"] = ["count", "capacity", "fill-rate.percent", "wasted.bytes", "retained.bytes"],
            ["heap.long-string"] = ["length", "shallow.bytes"],
            ["type.async-state-machine"] = ["instance.count", "shallow.bytes", "dominant-state", "gen2.count", "gen2.fraction", "is-async-void"],
            ["type.async-captured-closure"] = ["captured.bytes"],
            ["type.orphaned-task"] = ["shallow.bytes"],
            ["heap.static-root"] = ["retained.bytes", "objects-kept-alive.count", "gen2-or-loh-retained.fraction"],
            ["type.root-reachability"] = ["sample.bytes", "has-gc-root", "retained.bytes"],
            ["type.retention"] = ["instance.count", "shallow.bytes", "retained.bytes", "gen2.count"],
            ["event.publisher-leak"] = ["subscriber.count", "instance.count", "retained.bytes"],
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
