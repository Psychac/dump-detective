using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;

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
            ["root.kind-summary"] = ["kind.ordinal", "count", "retained.bytes"],
            ["root.population"] = ["total.count", "subgraph-walk-capped", "subgraph-walk-capped.count", "strong-handle.count", "finalizer-queue.count"],
            ["handle.pinned"] = ["retained.bytes"],
            ["handle.population"] = [
                "total.count", "strong-like.count", "pinned-target.count", "pinned-retained.bytes",
                "pinned-soh.count", "async-pinned-soh.count", "pinned-non-soh.count", "async-pinned-non-soh.count",
                "refcounted.count", "weakshort-gen0.count", "weakshort-gen1.count", "weakshort-gen2.count",
                "weakshort-loh.count", "weaklong-gen0.count", "weaklong-gen1.count", "weaklong-gen2.count",
                "weaklong-loh.count", "dependent.count", "dependent-resolved-edge.count", "dependent-unresolved-target.count",
                "dependent-unresolved.percent",
                "total-handles-warning.threshold", "pinned-handle-targets-warning.threshold",
                "pinned-retained-bytes-warning.threshold", "pinned-soh-warning.threshold", "refcounted-warning.threshold",
                "weaklong-gen2-fraction-warning.threshold", "weaklong-gen2-minimum-count.threshold",
                "dependent-unresolved-percent-warning.threshold",
            ],
            ["handle.refcounted-target"] = ["instance.count"],
            ["finalizer.queue-entry"] = ["retained.bytes", "shallow.bytes", "disposed-but-live", "is-critical-finalizer", "is-retained-exact"],
            ["type.finalizable-population"] = ["instance.count", "shallow.bytes", "gen2.count"],
            ["gc.finalizable-composition"] = ["total.count", "total.bytes", "gen2.count"],
            ["finalizer.queue-population"] = ["queue.count", "retained.bytes", "critical.count", "critical.bytes", "pressure.ratio", "is-retained-estimate-partial"],
            ["weakhandle.target"] = ["retained.bytes", "held-only-via-weak-reference"],
            ["weakreference.stale-wrapper"] = ["shallow.bytes"],
            ["weakreference.population"] = [
                "total-handles.count", "dead-targets.count", "object.count", "object.bytes",
                "dependent-dead-key.count", "held-only-detection-available",
            ],
            ["gc.generation-composition"] = [
                "gen0.bytes", "gen1.bytes", "gen2.bytes", "loh.bytes", "poh.bytes", "gen2.fraction", "loh.fraction",
                "gen0.count", "gen1.count", "gen2.count", "loh.count", "poh.count", "total.count",
                "loh.percent", "gen0.percent", "gen1.percent", "gen2.percent", "poh.percent",
                "loh-pressure.threshold", "gen0-pressure.threshold", "poh-pressure.threshold",
                "has-gen2-bytes", "finalizable-gen2.count", "finalizable-gen2.bytes",
            ],
            ["gc.generation-top-allocator"] = ["gen0.count"],
            ["gc.generation-top-accumulator"] = ["gen2.bytes", "gen2.count"],
            ["gc.generation-type-profile"] = ["total.bytes", "gen2.bytes", "gen0.count", "gen1.count", "gen2.count", "loh.count"],
            ["gc.allocation-composition"] = [
                "gen0.count-fraction", "gen1.count-fraction", "gen2.count-fraction", "loh.count-fraction",
                "gen0.size-fraction", "gen1.size-fraction", "gen2.size-fraction", "loh.size-fraction",
                "finalizable.type-count", "finalizable.bytes", "transient-classification.threshold",
            ],
            ["heap.segment-composition"] = ["generation.ordinal", "committed.bytes", "reserved.bytes", "used.bytes", "segment.count"],
            ["heap.memory-composition"] = [
                "total.bytes", "loh.bytes", "loh.percent", "total.count", "loh.count", "unique-types.count",
                "top1.percent", "top5.percent", "small-object.count-percent", "small-object.bytes-percent",
                "objects-per-mb", "top1-type.bytes",
            ],
            ["heap.loh-fragmentation"] = ["kind.ordinal", "segment.count", "total.bytes", "free.bytes", "used.bytes", "fragmentation.percent", "largest-free-block.bytes"],
            ["heap.loh-type-composition"] = ["loh.count", "loh.bytes"],
            ["heap.segment-reservation"] = [
                "committed.bytes", "reserved.bytes", "reservation-gap.bytes", "reserved-to-committed.ratio",
                "ephemeral.segment-count", "ephemeral.avg-fill-percent", "segment.count",
                "dump-pointer-size", "logical-heap.count", "ratio-high-pressure.threshold",
                "ratio-medium-pressure.threshold", "thirty-two-bit-pressure.threshold-bytes",
                "near-empty-region-fill-percent.threshold",
            ],
            ["heap.region-composition"] = ["region-kind.ordinal", "region.count", "reserved.bytes", "committed.bytes", "near-empty.count", "near-empty.committed-bytes"],
            ["thread.blocked"] = ["locks-held.count"],
            ["lock.contested"] = ["waiting.count", "recursion.count"],
            ["runtime.lock-pressure"] = ["held-lock.count"],
            ["thread.wait-state"] = ["locks-held.count", "circular-wait-candidate.count"],
            ["runtime.hang-pressure"] = ["alive-thread.count", "waiting-thread.count", "waiting.percent", "queued-work-items.count", "pending-task.count", "faulted-task.count", "task-continuation.count", "threadpool-max.count", "threadpool-active.count", "threadpool-cpu.percent", "high-threadpool-threshold.count"],
            ["thread.state"] = ["stack-size.bytes", "locks-held.count", "stack-root.count", "has-exception", "async-chain.depth"],
            ["runtime.thread-triage"] = ["alive-thread.count", "blocked-thread.count", "lock-holding-thread.count", "active-exception-thread.count", "active-processing-thread.count", "async-chain-thread.count", "max-async-chain.depth"],
            ["runtime.finalizer-thread"] = ["is-blocked", "locks-held.count"],
            ["thread.stack-cluster"] = ["cluster.count", "threadpool-worker.count", "gc.count", "finalizer.count"],
            ["runtime.thread-cluster-diversity"] = ["alive-thread.count", "unique-cluster.count", "diversity.percent"],
            ["infra.sql-connection-pool"] = ["current.size", "max.size", "min.size"],
            ["infra.sql-connection-pool-population"] = ["total.count", "near-capacity.count", "max-utilization.percent"],
            ["infra.sql-command"] = ["shallow.bytes"],
            ["infra.sql-command-population"] = ["total.count", "active.count", "disposed.count"],
            ["infra.sql-transaction"] = ["shallow.bytes"],
            ["infra.sql-transaction-population"] = ["total.count", "active.count", "disposed.count", "other.count"],
            ["infra.db-connection"] = ["generation.ordinal", "retained.bytes"],
            ["infra.db-connection-type-population"] = ["instance.count"],
            ["infra.db-connection-population"] = ["total.count", "open.count", "closed.count", "broken.count", "other.count", "unknown-state.count", "gen2-open.count", "gen0-open.count"],
            ["infra.http-object"] = ["shallow.bytes", "generation.ordinal", "connection-limit"],
            ["infra.http-handler-module"] = ["instance.count"],
            ["infra.http-object-population"] = [
                "total.count", "http-client.count", "http-web-request.count", "http-web-response.count",
                "http-message-handler.count", "service-point.count", "active-handler-tracking-entry.count",
                "expired-handler-tracking-entry.count", "http-client-gen0.count", "http-client-gen1.count",
                "http-client-gen2.count", "handler-client.ratio", "total.bytes",
            ],
            ["infra.timer"] = ["instance.count", "shallow.bytes"],
            ["infra.timer-population"] = [
                "total.count", "total.bytes", "threading.count", "timers-timer.count",
                "timer-queue.count", "holder.count", "periodic.count", "other.count",
                "top-type.has-root-path", "top-type.is-root-path-search-truncated", "top-type.evidence-signal.count",
            ],
            ["infra.wcf-channel"] = ["retained.bytes", "binding-hint.ordinal"],
            ["infra.wcf-channel-type-population"] = ["total.count"],
            ["infra.wcf-channel-population"] = [
                "total.count", "opening.count", "opened.count", "faulted.count", "closing.count", "closed.count",
                "other.count", "invalid-state.count", "duplex.count", "session.count", "factory.count",
            ],
            ["runtime.jit-activity"] = [
                "jit-heap.bytes", "active-methods.count", "distinct-methods.count", "tiered-methods.count",
                "managed-frame.count", "unmanaged-frame.count", "ready-to-run-frame.count", "dynamic-method-frame.count",
                "jit-manager.count", "large-method.count", "large-method.threshold-bytes",
                "top-method.hot-size-bytes", "top-method.cold-size-bytes",
                "max-thread-frame-depth.count", "max-thread-frame-depth.os-thread-id",
            ],
            ["runtime.jit-module-frames"] = ["active-frame.count"],
            ["heap.segment-kind-composition"] = ["kind.ordinal", "segment.count", "object.count", "committed.bytes", "reserved.bytes", "fragmented.bytes"],
            ["heap.poh-type-composition"] = ["instance.count", "shallow.bytes"],
            ["heap.frozen-type-composition"] = ["instance.count", "shallow.bytes"],
            ["heap.topology-population"] = [
                "is-server-gc", "logical-heap.count", "logical-heap.max-bytes", "logical-heap.min-bytes",
                "segment.count", "committed.bytes", "reserved.bytes", "reservation-gap.bytes",
                "soh.bytes", "loh.bytes", "loh.percent", "poh.bytes", "poh.percent"],
            ["runtime.module-heap-footprint"] = ["unique-type.count", "object.count", "shallow.bytes", "loh.bytes", "gen2.object-count"],
            ["runtime.module-population"] = [
                "total.count", "dynamic.count", "version-conflict.count", "heavy-module-warning.threshold-bytes",
                "total-dynamic.count", "total-domains.count", "anonymous-module.count", "unknown-identity-duplicate.count",
            ],
            ["runtime.module-conflict-group"] = [],
            ["runtime.module-type-density"] = ["unique-type.count", "shallow.bytes", "bytes-per-type.bytes"],
            ["runtime.module-cross-domain-load"] = ["domain.count", "shallow.bytes"],
            ["runtime.module-assemblyref-mismatch"] = [],
            ["runtime.module-unknown-identity"] = [],
            ["runtime.active-exception"] = ["generation.ordinal", "shallow.bytes"],
            ["runtime.crash-population"] = ["total.count", "active.count", "unique-types.count"],
            ["runtime.crash-thread-candidate"] = ["active-exception.count"],
            ["type.shape-composition"] = ["type.count", "avg-reference-fields", "gc-scan-work", "gen2-gc-scan-work", "reference-heavy-type.count", "value-heavy-type.count", "balanced-type.count"],
            ["type.reference-heavy"] = ["reference-field.count", "total-field.count", "instance.count"],
            ["type.value-heavy"] = ["reference-field.count", "total-field.count", "instance.count"],
            ["type.finalizable-reference-heavy"] = ["reference-field.count", "total-field.count", "instance.count"],
            ["type.boxing-pressure"] = ["total-boxed.count", "total-boxed.bytes", "avg-instance.bytes", "boxed-enum.count", "boxed-enum.bytes", "nullable-boxed.count", "nullable-boxed.bytes", "oversized-value.count", "padding-waste-type.count", "padding-aggregate.bytes", "gen2-boxed.count", "gen2.percent"],
            ["type.boxing"] = ["instance.count", "shallow.bytes", "gen0.count", "gen2.count", "gen2.fraction", "is-enum", "has-reference-fields", "has-iequatable", "is-nullable"],
            ["type.oversized-value"] = ["static-size.bytes", "instance.count"],
            ["type.struct-padding-waste"] = ["field.bytes", "struct-size.bytes", "wasted-padding.bytes", "waste.ratio"],
            ["type.array-composition"] = ["instance.count", "shallow.bytes", "gen2-plus-loh.percent", "avg-size.bytes", "array.rank", "loh.count", "loh.bytes", "pinned.count", "pinned.bytes"],
            ["type.array-population"] = ["total.count", "total.bytes", "loh.bytes", "pinned.count", "pinned.bytes"],
            ["heap.sparse-array"] = ["length", "null-or-zero.count", "sparse.ratio", "wasted.bytes"],
            ["heap.large-array"] = ["length", "shallow.bytes", "array.rank"],
            ["heap.pinned-array"] = ["length", "shallow.bytes", "array.rank"],
            ["collection.wasteful"] = ["count", "capacity", "fill-rate.percent", "wasted.bytes", "retained.bytes"],
            ["collection.waste-summary"] = ["total-collection.count", "total-wasted.bytes", "wasteful-collection.count"],
            ["collection.kind-waste"] = ["waste.count", "waste.bytes"],
            ["collection.element-type-waste"] = ["waste.count", "waste.bytes"],
            ["heap.long-string"] = ["length", "shallow.bytes"],
            ["type.string-composition"] = [
                "duplication.ratio", "duplicate-pattern.count", "duplicate-wasted.bytes", "loh-string.bytes",
                "very-long-string.count", "very-long-string.bytes", "pct-of-managed-heap.percent", "total-string-memory.bytes",
                "sampling-coverage.ratio", "strings-sampled.count", "total-string.count", "unique-pattern.count",
                "gen0.count", "gen1.count", "gen2.count",
            ],
            ["type.async-state-machine"] = ["instance.count", "shallow.bytes", "dominant-state", "gen2.count", "gen2.fraction", "is-async-void"],
            ["type.async-state-machine-population"] = ["total.count", "total.bytes", "gen2.count", "gen2.percent"],
            ["type.async-captured-closure"] = ["captured.bytes"],
            ["type.orphaned-task"] = ["shallow.bytes"],
            ["async.task-population"] = [
                "total.count", "pending.count", "pending-oldgen.count", "faulted.count", "canceled.count", "orphaned.count", "cycle-detected",
                "max-continuation-depth", "avg-continuation-depth", "total-tcs.count", "unresolved-tcs.count",
                "unresolved-tcs-oldgen.count", "total-vts.count", "pending-vts.count", "pending-vts-oldgen.count",
            ],
            ["heap.static-root"] = ["retained.bytes", "objects-kept-alive.count", "gen2-or-loh-retained.fraction"],
            ["static-root.population"] = [
                "root.count", "retained.bytes",
                "top-root.has-root-path", "top-root.is-root-path-search-truncated", "top-root.evidence-signal.count",
            ],
            ["type.root-reachability"] = ["sample.bytes", "has-gc-root", "retained.bytes"],
            ["reference-chain.type-sample"] = ["instance.count", "sample.count", "retained-sample.count", "dominant-root-kind.count"],
            ["reference-chain.population"] = ["analyzed.count", "retained.count", "retained.percent", "traversal-limited.count"],
            ["reference-chain.shared-root-group"] = ["type.count"],
            ["type.retention"] = ["instance.count", "shallow.bytes", "retained.bytes", "estimated-retained.bytes", "gen2.count"],
            ["type.retention-population"] = [
                "highly-referenced.count", "highly-referenced.bytes", "approximated-reference-addresses.count",
                "candidate.count", "analyzed.count", "retained.bytes", "pressure.ratio",
                "top-highly-referenced.has-root-path", "top-highly-referenced.is-root-path-search-truncated", "top-highly-referenced.evidence-signal.count",
            ],
            ["type.retention-hotspot"] = ["object.count", "total.bytes", "total-incoming-references.count"],
            ["event.publisher-leak"] = ["subscriber.count", "instance.count", "retained.bytes"],
            ["event.publisher-leak-group"] = [
                "is-static", "instance.count", "subscriber.count", "max-subscribers-per-instance.count",
                "max-severity.score", "is-timer-event", "is-property-changed-event",
            ],
            ["event.publisher-leak-population"] = [
                "total.count", "instance-leak.count", "static-leak.count",
                "top-static-publisher.has-root-path", "top-static-publisher.is-root-path-search-truncated", "top-static-publisher.evidence-signal.count",
                "top-instance-publisher.has-root-path", "top-instance-publisher.is-root-path-search-truncated", "top-instance-publisher.evidence-signal.count",
            ],
            ["handle.pinned-target-type"] = ["instance.count"],
            ["handle.dependent-target-type"] = ["instance.count"],
            ["handle.pinned-target-bytes"] = ["retained.bytes"],
            ["heap.loh-fragmentation-summary"] = [
                "segment.count", "total.bytes", "free.bytes", "used.bytes", "free-block.count",
                "fragmentation.percent", "largest-free-block.bytes", "free-gap.count", "sub-kb-free-gap.count",
            ],
            ["heap.static-root-retained-type"] = ["instance.count", "shallow.bytes"],
            ["leak.candidate-type"] = [
                "shallow.bytes", "instance.count", "gen2.percent", "gen2.count", "reference-field.ratio",
                "is-finalizable.flag", "is-container.flag", "is-static-rooted.flag", "is-delegate.flag",
            ],
            ["runtime.exception-type"] = ["instance.count"],
            ["runtime.crash-thread-candidate-module"] = ["active-exception.count"],
            ["runtime.module-disk-footprint"] = ["disk.bytes"],
            ["thread.stack-cluster-member"] = ["cluster.rank"],
            ["type.known-pattern-population"] = ["instance.count", "shallow.bytes"],
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

    /// <summary>
    /// Bidirectional: every <see cref="MeasureDirectionRegistry"/> entry (the runtime source of
    /// truth the differ actually reads) must appear in the JSON with the same value, and every
    /// <c>direction</c> the JSON declares must appear in <see cref="MeasureDirectionRegistry"/> —
    /// same shape as <see cref="CapabilityRegistry_ShouldMatchCapabilityVocabularyExactly"/>'s check
    /// against <see cref="CapabilityVocabulary"/>. Catches drift in either direction: a registry
    /// entry added without updating the JSON mirror, or a JSON edit that didn't update the registry
    /// the differ actually consults.
    /// </summary>
    [Fact]
    public void ObservationTypeRegistry_MeasureDirectionRegistry_MatchesJsonExactly()
    {
        JsonObject registry = LoadJsonObject("observation-type-registry.json");
        JsonObject observationTypes = registry["observationTypes"]!.AsObject();

        var jsonDirections = new Dictionary<(string ObservationType, string MeasureKey), string>();
        foreach ((string observationType, JsonNode? entryNode) in observationTypes)
        {
            JsonObject measures = entryNode!.AsObject()["measures"]!.AsObject();
            foreach ((string measureKey, JsonNode? measureNode) in measures)
            {
                if (measureNode!.AsObject().TryGetPropertyValue("direction", out JsonNode? directionNode))
                    jsonDirections[(observationType, measureKey)] = directionNode!.GetValue<string>();
            }
        }

        var registryDirections = MeasureDirectionRegistry.Known.ToDictionary(
            kvp => kvp.Key,
            kvp => JsonNamingPolicy.CamelCase.ConvertName(kvp.Value.ToString()));

        registryDirections.Should().BeEquivalentTo(jsonDirections,
            "MeasureDirectionRegistry.Known and observation-type-registry.json's per-measure 'direction' " +
            "fields must declare exactly the same set, with the same values");
    }

    [Fact]
    public void ObservationSchema_ShouldBeWellFormedAndCoverKnownObservationTypeAsFreeForm()
    {
        JsonObject schema = LoadJsonObject("observation.schema.json");

        schema["properties"]!["observationType"]!["type"]!.GetValue<string>().Should().Be("string",
            "ObservationType is deliberately free-form (observation-and-correlation-model.md § 7) — " +
            "the closed list of real values lives in observation-type-registry.json, not as a schema enum");
    }

    /// <summary>
    /// Keeps <see cref="KnownEmittedObservationTypes"/> honest: every <c>ObservationType = "..."</c>
    /// literal under <c>src/</c> must appear in it. The table is hand-maintained (measure keys can't
    /// be reliably scanned), and 11 types added during phase 5 step 6 slipped past both it and the
    /// registry before this check existed.
    /// </summary>
    [Fact]
    public void KnownEmittedObservationTypes_ShouldCoverEveryObservationTypeEmittedInSource()
    {
        string sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
        var emittedInSource = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;

            foreach (Match match in EmittedObservationTypePattern.Matches(File.ReadAllText(file)))
                emittedInSource.Add(match.Groups[1].Value);
        }

        emittedInSource.Should().NotBeEmpty();
        emittedInSource.Where(type => !KnownEmittedObservationTypes.ContainsKey(type)).Should().BeEmpty(
            "every observation type an analyzer emits must be listed in KnownEmittedObservationTypes " +
            "and registered in observation-type-registry.json");
    }

    private static readonly Regex EmittedObservationTypePattern = new("ObservationType = \"([a-z0-9.-]+)\"", RegexOptions.Compiled);

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
