using System.Reflection;

using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// The known capability vocabulary, per docs/refactor/modularity/source-model.md § 3. This is the
/// compile-time source of truth (a future analyzer needs a <c>const string</c> to pass to
/// <c>[RequiresCapability(...)]</c>, which a JSON file can't provide); the checked-in, versioned
/// mirror <c>schema/DumpDetective.Schema/capability-registry.json</c> landed in Phase 1 and is kept
/// in sync by <c>SdkRegistryConformanceTests.CapabilityRegistry_ShouldMatchCapabilityVocabularyExactly</c>.
/// After adding a field here, run <c>dotnet run --project tools/CapabilityRegistryAudit</c> to
/// scaffold the matching registry entry (it derives the mechanical fields and flags the rest for
/// curation) instead of hand-writing the JSON — grouped here by the artifact kind that provides
/// them, not by the analyzer that consumes them.
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

    /// <summary>GC-root reachability paths ("is this object reachable, and via what chain?") —
    /// added 2026-09-11 for <c>DbConnectionAnalyzer</c>/<c>TimerLeakAnalyzer</c>'s retyping; see
    /// <see cref="Analysis.IHeapRootPathQuery"/>'s own remarks.</summary>
    public const string HeapRootPaths = "heap.root-paths";

    // Runtime (dump-provided)
    public const string RuntimeModules = "runtime.modules";
    public const string RuntimeThreads = "runtime.threads";
    public const string RuntimeStacks = "runtime.stacks";
    public const string RuntimeExceptions = "runtime.exceptions";
    public const string RuntimeJit = "runtime.jit";

    /// <summary>Thread-pool state (min/max/active/idle worker threads, queue length, CPU
    /// utilization) — added 2026-09-12 for <c>HangAnalyzer</c>'s retyping; see
    /// <see cref="Analysis.IRuntimeThreadPoolQuery"/>'s own remarks.</summary>
    public const string RuntimeThreadPool = "runtime.thread-pool";

    /// <summary>Live monitor locks (<c>lock</c>/<c>Monitor.Enter</c>). Was declared but unconsumed
    /// until 2026-09-09, when <see cref="Analysis.IHeapSyncBlockQuery"/> became its first real
    /// consumer, backing <c>LockGraphAnalyzer</c>'s <c>heap.EnumerateSyncBlocks()</c> usage — see
    /// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. Named <c>runtime.*</c>
    /// rather than <c>heap.*</c> despite sync blocks physically living on the object header, since
    /// what they represent is runtime lock state, not object data.</summary>
    public const string RuntimeLocks = "runtime.locks";

    /// <summary>Tier 2 (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
    /// per-field value reads off a live heap object, backing <see cref="Analysis.IDumpObjectFieldQuery"/>.
    /// Dump-only forever — no trace source will ever satisfy this, unlike every other capability
    /// above, which is source-neutral in shape even though only a dump implements it today. First
    /// real consumer: <c>SqlConnectionPoolAnalyzer</c>, added 2026-09-11.</summary>
    public const string DumpObjectFields = "dump.object-fields";

    /// <summary>Tier 2, second dump-only capability alongside <see cref="DumpObjectFields"/> — array
    /// element shape/sparse-counting, backing <see cref="Analysis.IDumpArrayShapeQuery"/>. Dump-only
    /// forever for the same reason <see cref="DumpObjectFields"/> is. First real consumer:
    /// <c>ArrayAnalyzer</c>, added 2026-09-11.</summary>
    public const string DumpArrayElements = "dump.array-elements";

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

    // No TemporalSeries — removed 2026-09-10, same reasoning as TemporalKind.Series's removal (see
    // docs/refactor/modularity/phase-1-sdk-review-findings.md item 11 and its follow-on note): a
    // Capability is declared per-artifact (ArtifactDescriptor.Provides), and no single artifact can
    // provide "a series" any more than a single Observation can. Zero real usage anywhere before
    // removing it. If a session-level capability concept (AnalysisSession.AvailableCapabilities
    // computing something no individual artifact provides) turns out to be real once Phase 4
    // exists, reintroduce it there with actual grounding — not kept here on a guess.

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
