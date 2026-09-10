using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// Capability-scoped replacement for <c>Core.Models.AnalysisContext</c> — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md for why this cannot simply be
/// the existing type moved (it carries <c>ClrRuntime</c> by a deliberate Phase 7 boundary decision,
/// which an SDK type with zero <c>PackageReference</c>s cannot). Every surface is nullable —
/// <c>null</c> means the capability wasn't available for this session, which an analyzer's
/// <see cref="RequiresCapabilityAttribute"/>/<see cref="OptionalCapabilityAttribute"/> declarations
/// should already have made non-surprising by the time <c>AnalyzeAsync</c> runs.
/// </summary>
/// <remarks>
/// Deliberately does not yet carry <c>AnalysisOptions</c>/<c>DiagnosticsOptions</c>/
/// <c>IAnalysisDiagnosticsSink</c>. <c>Core.Options.AnalysisOptions</c> is a 23-sub-record,
/// reflection-keyed options bag tied to every individual analyzer's own option type — mirroring it
/// wholesale here would mean porting 23 more types on a guess, not a measured need. Left out until
/// the pilot/batch retyping migration shows exactly what a capability-scoped analyzer actually needs
/// from it, per this project's hard-need-basis convention.
///
/// <b>Fixed named properties, not a generic <c>TryGetCapability&lt;T&gt;()</c> resolver — considered
/// and rejected 2026-09-10</b> (docs/refactor/modularity/phase-1-sdk-review-findings.md item 12).
/// The "avoid hardcoded catalogs" concern that motivates Phase 3's attribute-driven analyzer
/// discovery doesn't transfer here: that concern is about the *analyzer* count, a large,
/// plugin-extensible set that genuinely can't be hardcoded. Capability *surfaces* (the properties
/// below) are a different, much smaller axis — SDK-curated, growing only when the framework itself
/// gains a new kind of coarse query, never invented by a third-party plugin. A generic resolver
/// would trade real IntelliSense/type-safety for an extensibility need that doesn't exist.
///
/// <b>Required-vs-optional nullability is not distinguished at the type level — deliberately
/// deferred, not fixed.</b> Whether e.g. <see cref="HeapObjects"/> is actually guaranteed non-null
/// for a given analyzer run depends entirely on whether the orchestrator filtered that analyzer in
/// because its <see cref="RequiresCapabilityAttribute"/> was satisfied — a promise only the
/// orchestrator can make, and it doesn't exist yet (Phase 4). This type is just a data bag; nothing
/// here can encode how it was populated. Every property stays uniformly nullable, and every analyzer
/// body null-checks/null-forgives regardless of its own declared requirements, until Phase 4's
/// orchestrator exists to design a real guarantee against.
/// </remarks>
public sealed class AnalysisContext
{
    public required IObservationSink Observations { get; init; }

    public IProgress<AnalyzerProgressReport>? Progress { get; set; }

    public IHeapObjectStream? HeapObjects { get; init; }
    public IHeapObjectLookup? HeapObjectLookup { get; init; }
    public IHeapRootQuery? HeapRoots { get; init; }
    public IHeapHandleQuery? HeapHandles { get; init; }
    public IHeapSegmentQuery? HeapSegments { get; init; }
    public IHeapFinalizerQueueQuery? HeapFinalizerQueue { get; init; }
    public IHeapSyncBlockQuery? HeapSyncBlocks { get; init; }
    public IHeapTypeStatisticsQuery? HeapTypeStatistics { get; init; }
    public IHeapReferenceQuery? HeapReferences { get; init; }
    public IHeapReverseReferenceQuery? HeapReverseReferences { get; init; }
    public IHeapReachabilityQuery? HeapReachability { get; init; }
    public IHeapDominatorQuery? HeapDominators { get; init; }
    public IRuntimeThreadQuery? RuntimeThreads { get; init; }
    public IRuntimeModuleQuery? RuntimeModules { get; init; }
    public IRuntimeJitQuery? RuntimeJit { get; init; }
}
