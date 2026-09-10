using System.Reflection;

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

    /// <summary>Reachability-from-root facts — added 2026-09-09, split out from
    /// <see cref="HeapDominators"/> 2026-09-10 once it turned out to be backed by a genuinely
    /// different, independently-gated build stage (Stage A, not Stage B); see
    /// <see cref="Analysis.IHeapReachabilityQuery"/>'s own remarks.</summary>
    public const string HeapReachability = "heap.reachability";

    /// <summary>Dominator-tree-derived facts (retained size, immediate dominator, thread retention)
    /// — added 2026-09-09, narrowed to Stage-B-only 2026-09-10 (reachability split out to
    /// <see cref="HeapReachability"/> above). Kept as one capability rather than three since all
    /// three are backed by the same Stage B build and gated together in practice; see
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

    /// <summary>
    /// Every capability named above, for build-time/test-time validation that a declared capability
    /// isn't a typo against this vocabulary.
    /// </summary>
    /// <remarks>
    /// Reflected over this class's own <c>const string</c> fields rather than hand-listed —
    /// considered and switched 2026-09-10
    /// (docs/refactor/modularity/phase-1-sdk-review-findings.md item 14) after hitting the drift
    /// this was meant to prevent twice in one session (adding <see cref="HeapDominators"/>, then
    /// <see cref="HeapReachability"/>, each requiring a separate edit here that
    /// <c>SdkRegistryConformanceTests</c> had to catch after the fact). These fields must stay
    /// <c>const</c>, not <c>static readonly</c> — so a future analyzer can write
    /// <c>[RequiresCapability(CapabilityVocabulary.HeapObjects)]</c>, a compile-time-constant
    /// attribute argument, instead of a raw string literal — which rules out any non-reflection way
    /// to enumerate them; this isn't a workaround, it's the only mechanism that matches the
    /// constraint the type already has for a good reason. A source generator would do the same job
    /// with zero runtime cost, but is disproportionate ceremony for one ~30-member class; revisit
    /// only if this exact pattern needs repeating across many types.
    /// </remarks>
    public static readonly IReadOnlySet<string> Known = typeof(CapabilityVocabulary)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}
