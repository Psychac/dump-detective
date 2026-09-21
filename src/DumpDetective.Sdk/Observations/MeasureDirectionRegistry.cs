namespace DumpDetective.Sdk.Observations;

/// <summary>
/// The known <c>(ObservationType, measure key) → MeasureDirection</c> mapping — this is the
/// compile-time source of truth a runtime differ reads (a JSON file can't be queried without a
/// loader, and this project has no precedent for loading a schema registry at runtime; see
/// <see cref="CapabilityVocabulary"/>'s equivalent split with `capability-registry.json`, the same
/// pattern followed here). The checked-in, versioned mirror
/// <c>schema/DumpDetective.Schema/observation-type-registry.json</c>'s per-measure <c>direction</c>
/// field is kept in sync by
/// <c>SdkRegistryConformanceTests.ObservationTypeRegistry_MeasureDirectionRegistry_MatchesJsonExactly</c>.
/// </summary>
/// <remarks>
/// Mined from the 36 <c>IAnalyzerTrendComparer</c> implementations' existing
/// <c>MetricTrendDirection</c> values, one <c>Plugins.*</c> package at a time — not guessed. A
/// measure with no matching comparer metric is absent here, not defaulted to <see cref="MeasureDirection.Neutral"/>;
/// an absent entry means "not yet classified." Headline (unscoped) measures only — see
/// docs/refactor/modularity/phase-5-observations-synthesis.md ## Work step 4 for why scoped
/// (per-type) measures are explicitly out of this table's scope.
/// </remarks>
public static class MeasureDirectionRegistry
{
    /// <summary>All known directions, keyed by <c>(ObservationType, measure key)</c>.</summary>
    public static readonly IReadOnlyDictionary<(string ObservationType, string MeasureKey), MeasureDirection> Known =
        new Dictionary<(string, string), MeasureDirection>
        {
            // Plugins.Memory
            [("gc.allocation-composition", "gen0.count-fraction")] = MeasureDirection.Neutral,
            [("gc.allocation-composition", "gen1.count-fraction")] = MeasureDirection.Neutral,
            [("gc.allocation-composition", "gen2.count-fraction")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "loh.count-fraction")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "gen0.size-fraction")] = MeasureDirection.Neutral,
            [("gc.allocation-composition", "gen2.size-fraction")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "loh.size-fraction")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "finalizable.type-count")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "finalizable.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.allocation-composition", "transient-classification.threshold")] = MeasureDirection.Neutral,

            [("gc.generation-composition", "gen0.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "gen1.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "gen2.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "loh.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "poh.bytes")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "gen0.count")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "poh.count")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "total.count")] = MeasureDirection.Neutral,
            [("gc.generation-composition", "loh.percent")] = MeasureDirection.HigherIsWorse,
            [("gc.generation-composition", "loh-pressure.threshold")] = MeasureDirection.Neutral,
            [("gc.generation-composition", "gen0-pressure.threshold")] = MeasureDirection.Neutral,
            [("gc.generation-composition", "poh-pressure.threshold")] = MeasureDirection.Neutral,

            [("heap.loh-fragmentation", "fragmentation.percent")] = MeasureDirection.HigherIsWorse,
            [("heap.loh-fragmentation", "free.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.loh-fragmentation", "total.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.loh-fragmentation", "largest-free-block.bytes")] = MeasureDirection.LowerIsWorse,
            [("heap.loh-fragmentation", "segment.count")] = MeasureDirection.Neutral,

            [("heap.memory-composition", "total.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.memory-composition", "loh.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.memory-composition", "loh.percent")] = MeasureDirection.HigherIsWorse,
            [("heap.memory-composition", "unique-types.count")] = MeasureDirection.Neutral,

            [("heap.segment-reservation", "committed.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.segment-reservation", "reserved.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.segment-reservation", "reservation-gap.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.segment-reservation", "reserved-to-committed.ratio")] = MeasureDirection.HigherIsWorse,
            [("heap.segment-reservation", "ephemeral.avg-fill-percent")] = MeasureDirection.HigherIsWorse,
            [("heap.segment-reservation", "ratio-high-pressure.threshold")] = MeasureDirection.Neutral,
            [("heap.segment-reservation", "ratio-medium-pressure.threshold")] = MeasureDirection.Neutral,
            [("heap.segment-reservation", "thirty-two-bit-pressure.threshold-bytes")] = MeasureDirection.Neutral,
            [("heap.segment-reservation", "near-empty-region-fill-percent.threshold")] = MeasureDirection.Neutral,

            // Plugins.Gc
            [("finalizer.queue-population", "queue.count")] = MeasureDirection.HigherIsWorse,

            [("handle.population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "strong-like.count")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "pinned-target.count")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "pinned-retained.bytes")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "dependent.count")] = MeasureDirection.Neutral,
            [("handle.population", "dependent-unresolved-target.count")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "dependent-unresolved.percent")] = MeasureDirection.HigherIsWorse,
            [("handle.population", "total-handles-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "pinned-handle-targets-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "pinned-retained-bytes-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "pinned-soh-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "refcounted-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "weaklong-gen2-fraction-warning.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "weaklong-gen2-minimum-count.threshold")] = MeasureDirection.Neutral,
            [("handle.population", "dependent-unresolved-percent-warning.threshold")] = MeasureDirection.Neutral,

            [("root.population", "total.count")] = MeasureDirection.Neutral,
            [("root.population", "subgraph-walk-capped.count")] = MeasureDirection.Neutral,

            [("weakreference.population", "total-handles.count")] = MeasureDirection.HigherIsWorse,
            [("weakreference.population", "dead-targets.count")] = MeasureDirection.HigherIsWorse,
            [("weakreference.population", "object.bytes")] = MeasureDirection.HigherIsWorse,
            [("weakreference.population", "dependent-dead-key.count")] = MeasureDirection.HigherIsWorse,

            // Plugins.Threads
            [("runtime.hang-pressure", "alive-thread.count")] = MeasureDirection.Neutral,
            [("runtime.hang-pressure", "waiting-thread.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.hang-pressure", "waiting.percent")] = MeasureDirection.HigherIsWorse,
            [("runtime.hang-pressure", "queued-work-items.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.hang-pressure", "pending-task.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.hang-pressure", "faulted-task.count")] = MeasureDirection.HigherIsWorse,

            [("runtime.lock-pressure", "held-lock.count")] = MeasureDirection.Neutral,

            [("runtime.thread-cluster-diversity", "alive-thread.count")] = MeasureDirection.Neutral,
            [("runtime.thread-cluster-diversity", "unique-cluster.count")] = MeasureDirection.Neutral,
            [("runtime.thread-cluster-diversity", "diversity.percent")] = MeasureDirection.LowerIsWorse,

            [("runtime.thread-triage", "alive-thread.count")] = MeasureDirection.Neutral,
            [("runtime.thread-triage", "blocked-thread.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.thread-triage", "lock-holding-thread.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.thread-triage", "active-exception-thread.count")] = MeasureDirection.HigherIsWorse,

            // Plugins.Async
            [("async.task-population", "total.count")] = MeasureDirection.Neutral,
            [("async.task-population", "pending.count")] = MeasureDirection.HigherIsWorse,
            [("async.task-population", "faulted.count")] = MeasureDirection.HigherIsWorse,
            [("async.task-population", "canceled.count")] = MeasureDirection.Neutral,
            [("async.task-population", "orphaned.count")] = MeasureDirection.HigherIsWorse,
            [("async.task-population", "max-continuation-depth")] = MeasureDirection.HigherIsWorse,
            [("async.task-population", "avg-continuation-depth")] = MeasureDirection.HigherIsWorse,

            [("type.async-state-machine-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("type.async-state-machine-population", "total.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.async-state-machine-population", "gen2.count")] = MeasureDirection.HigherIsWorse,
            [("type.async-state-machine-population", "gen2.percent")] = MeasureDirection.HigherIsWorse,

            // Plugins.Types
            [("type.boxing-pressure", "total-boxed.count")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "total-boxed.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "boxed-enum.count")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "boxed-enum.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "nullable-boxed.count")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "nullable-boxed.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "oversized-value.count")] = MeasureDirection.HigherIsWorse,
            [("type.boxing-pressure", "gen2-boxed.count")] = MeasureDirection.HigherIsWorse,

            [("collection.waste-summary", "total-collection.count")] = MeasureDirection.Neutral,
            [("collection.waste-summary", "total-wasted.bytes")] = MeasureDirection.HigherIsWorse,
            [("collection.waste-summary", "wasteful-collection.count")] = MeasureDirection.HigherIsWorse,

            [("type.shape-composition", "type.count")] = MeasureDirection.Neutral,
            [("type.shape-composition", "avg-reference-fields")] = MeasureDirection.HigherIsWorse,
            [("type.shape-composition", "gc-scan-work")] = MeasureDirection.HigherIsWorse,
            [("type.shape-composition", "gen2-gc-scan-work")] = MeasureDirection.HigherIsWorse,
            [("type.shape-composition", "reference-heavy-type.count")] = MeasureDirection.HigherIsWorse,
            [("type.shape-composition", "value-heavy-type.count")] = MeasureDirection.Neutral,
            [("type.shape-composition", "balanced-type.count")] = MeasureDirection.Neutral,

            [("type.string-composition", "total-string-memory.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.string-composition", "duplicate-pattern.count")] = MeasureDirection.HigherIsWorse,
            [("type.string-composition", "duplicate-wasted.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.string-composition", "duplication.ratio")] = MeasureDirection.HigherIsWorse,
            [("type.string-composition", "loh-string.bytes")] = MeasureDirection.HigherIsWorse,
            [("type.string-composition", "pct-of-managed-heap.percent")] = MeasureDirection.HigherIsWorse,

            // Plugins.Leaks
            [("type.retention-population", "highly-referenced.count")] = MeasureDirection.HigherIsWorse,

            [("event.publisher-leak-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("event.publisher-leak-population", "instance-leak.count")] = MeasureDirection.HigherIsWorse,
            [("event.publisher-leak-population", "static-leak.count")] = MeasureDirection.HigherIsWorse,

            [("reference-chain.population", "retained.percent")] = MeasureDirection.HigherIsWorse,
            [("reference-chain.population", "retained.count")] = MeasureDirection.HigherIsWorse,

            [("static-root.population", "root.count")] = MeasureDirection.HigherIsWorse,
            [("static-root.population", "retained.bytes")] = MeasureDirection.HigherIsWorse,

            // Plugins.Runtime
            [("runtime.crash-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.crash-population", "active.count")] = MeasureDirection.HigherIsWorse,

            [("runtime.jit-activity", "jit-heap.bytes")] = MeasureDirection.HigherIsWorse,
            [("runtime.jit-activity", "jit-manager.count")] = MeasureDirection.Neutral,
            [("runtime.jit-activity", "active-methods.count")] = MeasureDirection.Neutral,
            [("runtime.jit-activity", "distinct-methods.count")] = MeasureDirection.Neutral,
            [("runtime.jit-activity", "tiered-methods.count")] = MeasureDirection.Neutral,
            [("runtime.jit-activity", "unmanaged-frame.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.jit-activity", "ready-to-run-frame.count")] = MeasureDirection.Neutral,
            [("runtime.jit-activity", "dynamic-method-frame.count")] = MeasureDirection.HigherIsWorse,

            [("runtime.module-population", "total.count")] = MeasureDirection.Neutral,
            [("runtime.module-population", "dynamic.count")] = MeasureDirection.Neutral,
            [("runtime.module-population", "version-conflict.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.module-population", "total-domains.count")] = MeasureDirection.Neutral,
            [("runtime.module-population", "total-dynamic.count")] = MeasureDirection.HigherIsWorse,
            [("runtime.module-population", "heavy-module-warning.threshold-bytes")] = MeasureDirection.Neutral,

            [("heap.topology-population", "segment.count")] = MeasureDirection.Neutral,
            [("heap.topology-population", "committed.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "reserved.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "reservation-gap.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "soh.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "loh.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "loh.percent")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "poh.bytes")] = MeasureDirection.HigherIsWorse,
            [("heap.topology-population", "poh.percent")] = MeasureDirection.HigherIsWorse,

            // Plugins.Infra
            [("infra.db-connection-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("infra.db-connection-population", "open.count")] = MeasureDirection.HigherIsWorse,
            [("infra.db-connection-population", "closed.count")] = MeasureDirection.Neutral,
            [("infra.db-connection-population", "broken.count")] = MeasureDirection.HigherIsWorse,
            [("infra.db-connection-population", "unknown-state.count")] = MeasureDirection.HigherIsWorse,
            [("infra.db-connection-population", "gen2-open.count")] = MeasureDirection.HigherIsWorse,
            [("infra.db-connection-population", "gen0-open.count")] = MeasureDirection.HigherIsWorse,

            [("infra.http-object-population", "http-web-response.count")] = MeasureDirection.HigherIsWorse,
            [("infra.http-object-population", "http-message-handler.count")] = MeasureDirection.HigherIsWorse,

            [("infra.sql-command-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("infra.sql-command-population", "active.count")] = MeasureDirection.HigherIsWorse,
            [("infra.sql-command-population", "disposed.count")] = MeasureDirection.Neutral,

            [("infra.sql-transaction-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("infra.sql-transaction-population", "disposed.count")] = MeasureDirection.Neutral,
            [("infra.sql-transaction-population", "other.count")] = MeasureDirection.HigherIsWorse,

            [("infra.wcf-channel-population", "total.count")] = MeasureDirection.HigherIsWorse,
            [("infra.wcf-channel-population", "opening.count")] = MeasureDirection.HigherIsWorse,
            [("infra.wcf-channel-population", "opened.count")] = MeasureDirection.Neutral,
            [("infra.wcf-channel-population", "faulted.count")] = MeasureDirection.HigherIsWorse,
            [("infra.wcf-channel-population", "closing.count")] = MeasureDirection.HigherIsWorse,

            [("infra.sql-connection-pool-population", "total.count")] = MeasureDirection.Neutral,
            [("infra.sql-connection-pool-population", "near-capacity.count")] = MeasureDirection.HigherIsWorse,
            [("infra.sql-connection-pool-population", "max-utilization.percent")] = MeasureDirection.HigherIsWorse,
        };

    /// <summary>Looks up a measure's direction. False (with <paramref name="direction"/> unset) for
    /// a measure not yet classified — the caller's job to decide what "no known direction" means for
    /// its use case, never silently defaulted to <see cref="MeasureDirection.Neutral"/> here.</summary>
    public static bool TryGetDirection(string observationType, string measureKey, out MeasureDirection direction) =>
        Known.TryGetValue((observationType, measureKey), out direction);
}
