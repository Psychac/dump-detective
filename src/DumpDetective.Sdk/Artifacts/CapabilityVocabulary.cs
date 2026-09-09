using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// The known capability vocabulary, per docs/refactor/modularity/source-model.md § 3. Interim
/// source of truth until the checked-in <c>capability-registry.json</c> lands (deferred — see
/// docs/refactor/modularity-plan.md § 10 point 4); grouped by the artifact kind that provides them,
/// not by the analyzer that consumes them.
/// </summary>
public static class CapabilityVocabulary
{
    // Heap (dump-provided)
    public const string HeapObjects = "heap.objects";
    public const string HeapTypes = "heap.types";
    public const string HeapRoots = "heap.roots";
    public const string HeapReferences = "heap.references";
    public const string HeapReverseReferences = "heap.reverse-references";
    public const string HeapGenerations = "heap.generations";
    public const string HeapSegments = "heap.segments";
    public const string HeapHandles = "heap.handles";
    public const string HeapStatics = "heap.statics";
    public const string HeapStrings = "heap.strings";
    public const string HeapFinalizerQueue = "heap.finalizer-queue";

    /// <summary>Dominator-tree-derived facts (retained size, immediate dominator, reachability,
    /// thread retention) — added 2026-09-09. Kept as one capability rather than four separate ones
    /// since all are backed by the same Stage A/B build and gated together in practice; see
    /// <see cref="Analysis.IHeapDominatorQuery"/>'s own remarks.</summary>
    public const string HeapDominators = "heap.dominators";

    // Runtime (dump-provided)
    public const string RuntimeModules = "runtime.modules";
    public const string RuntimeThreads = "runtime.threads";
    public const string RuntimeStacks = "runtime.stacks";
    public const string RuntimeExceptions = "runtime.exceptions";
    public const string RuntimeJit = "runtime.jit";

    /// <summary>Live monitor locks (<c>lock</c>/<c>Monitor.Enter</c>). Was declared but unconsumed
    /// until 2026-09-09, when <see cref="Analysis.IHeapSyncBlockQuery"/> became its first real
    /// consumer, backing <c>LockGraphAnalyzer</c>'s <c>heap.EnumerateSyncBlocks()</c> usage — see
    /// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. Named <c>runtime.*</c>
    /// rather than <c>heap.*</c> despite sync blocks physically living on the object header, since
    /// what they represent is runtime lock state, not object data.</summary>
    public const string RuntimeLocks = "runtime.locks";

    // Trace-provided
    public const string TraceCpuSamples = "trace.cpu-samples";
    public const string TraceGcEvents = "trace.gc-events";
    public const string TraceAllocSamples = "trace.alloc-samples";
    public const string TraceContentionEvents = "trace.contention-events";
    public const string TraceExceptionEvents = "trace.exception-events";
    public const string TraceThreadTimeline = "trace.thread-timeline";
    public const string TraceJitEvents = "trace.jit-events";
    public const string TraceHttpEvents = "trace.http-events";
    public const string TraceCustomEvents = "trace.custom-events";

    // Temporal
    public const string TemporalPoint = "temporal.point";
    public const string TemporalInterval = "temporal.interval";
    public const string TemporalSeries = "temporal.series";

    /// <summary>Every capability named above, for build-time/test-time validation that a
    /// declared capability isn't a typo against this vocabulary.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        HeapObjects, HeapTypes, HeapRoots, HeapReferences, HeapReverseReferences, HeapGenerations,
        HeapSegments, HeapHandles, HeapStatics, HeapStrings, HeapFinalizerQueue, HeapDominators,
        RuntimeModules, RuntimeThreads, RuntimeStacks, RuntimeExceptions, RuntimeJit, RuntimeLocks,
        TraceCpuSamples, TraceGcEvents, TraceAllocSamples, TraceContentionEvents,
        TraceExceptionEvents, TraceThreadTimeline, TraceJitEvents, TraceHttpEvents, TraceCustomEvents,
        TemporalPoint, TemporalInterval, TemporalSeries,
    };
}
