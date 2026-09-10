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
/// Deliberately does not carry <c>DiagnosticsOptions</c>/<c>IAnalysisDiagnosticsSink</c>, and does
/// not mirror <c>Core.Options.AnalysisOptions</c>'s 23-sub-record, reflection-keyed shape wholesale
/// — that would mean porting 23 more types on a guess, not a measured need. What the pilot
/// (<c>GCGenerationAnalyzer</c>, docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md)
/// did measurably need is a way for an analyzer to receive its own narrowly-typed options record:
/// see <see cref="AnalyzerOptions"/>.
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

    /// <summary>
    /// This analyzer's own options record, or <c>null</c> when it declares none. Untyped —
    /// analyzer-specific option types live outside the SDK (today, in <c>Core.Options</c>, which the
    /// SDK cannot reference) — the analyzer casts to its own known type. Deliberately a single slot,
    /// not a keyed bag: only one analyzer needs this so far (the pilot), and a real multi-analyzer
    /// options registry would be speculative design ahead of a second measured need. Revisit once
    /// the batch migration (phase-1-full-extraction-retyping-plan.md) shows what a shared shape
    /// should look like.
    /// </summary>
    public object? AnalyzerOptions { get; init; }

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
