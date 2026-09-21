namespace DumpDetective.Analysis.Trend;

/// <summary>
/// Which analyzer owns each headline <c>ObservationType</c> that <see cref="ObservationTrendDiffer"/>
/// covers — needed to attribute the differ's flat <c>(ObservationType, measure key)</c>-keyed
/// output back to a per-analyzer <see cref="AnalyzerTrendResult"/>, the shape
/// <c>TrendReportComposer</c> and everything downstream of it already consumes. Hand-maintained,
/// same style as <see cref="Sdk.Observations.MeasureDirectionRegistry"/> — lists every analyzer
/// whose trend comparer has fully retired onto the generic differ (all-headline, and with at least
/// one registry-backed measure; see docs/refactor/modularity/phase-5-observations-synthesis.md
/// ## Work step 4's per-package CHANGELOG entries for history). AsyncStateMachine, HeapTopology,
/// and SqlConnectionPool joined last, once their analyzer started emitting the whole-run aggregate
/// each had been missing (schema/DumpDetective.Schema/CHANGELOG.md 1.69.0) — the last 3 of the 11
/// all-headline comparers, closing that gap.
///
/// The 6 comparers below are the first "mixed" retirements (Bucket A of
/// docs/refactor/modularity/phase-5-step4-comparer-retirement-audit.md): only their headline
/// branches moved here — each comparer stays registered in the DI-resolved
/// <c>IAnalyzerTrendComparer</c> list for its remaining scoped (per-type/per-entity) branches.
/// </summary>
internal static class ObservationDifferAnalyzerOwnership
{
    public static readonly IReadOnlyDictionary<string, string> ObservationTypeToAnalyzerName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["weakreference.population"] = "Weak Reference Analysis",
            ["event.publisher-leak-population"] = "Event Leak Analysis",
            ["reference-chain.population"] = "Reference Chain Analysis",
            ["heap.loh-fragmentation"] = "LOH & POH Fragmentation Analysis",
            ["heap.segment-reservation"] = "Segment Reservation Analysis",
            ["runtime.jit-activity"] = "JIT Analysis",
            ["runtime.lock-pressure"] = "Lock Graph Analysis",
            ["runtime.thread-cluster-diversity"] = "Thread Stack Signature Clustering",
            ["type.async-state-machine-population"] = "Async State Machine Analysis",
            ["heap.topology-population"] = "Heap Topology",
            ["infra.sql-connection-pool-population"] = "SQL Connection Pool Analysis",
            ["static-root.population"] = "Static Root Leak Detection",
            ["collection.waste-summary"] = "Collection Analysis",
            ["runtime.crash-population"] = "Crash Analysis",
            ["runtime.module-population"] = "Module Analysis",
            ["runtime.thread-triage"] = "Thread Analysis",
            ["heap.memory-composition"] = "Memory Analysis",
            ["runtime.hang-pressure"] = "Hang Analysis",
            ["infra.wcf-channel-population"] = "WCF Channel Analysis",
            ["type.shape-composition"] = "Object Shape Analysis",
            ["handle.population"] = "GC Handle Analysis",
            ["async.task-population"] = "Async Task Analysis",
            ["infra.db-connection-population"] = "DB Connection Analysis",
            ["infra.sql-command-population"] = "SQL Command Analysis",
            ["infra.sql-transaction-population"] = "SQL Transaction Analysis",
            ["type.boxing-pressure"] = "Boxing Analysis",
            ["root.population"] = "GC Root Analysis",
            ["gc.allocation-composition"] = "Allocation Pattern Analysis",
            ["type.array-population"] = "Array Analysis",
        };
}
