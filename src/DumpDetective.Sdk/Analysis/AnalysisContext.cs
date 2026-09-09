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
    public IHeapDominatorQuery? HeapDominators { get; init; }
    public IRuntimeThreadQuery? RuntimeThreads { get; init; }
    public IRuntimeModuleQuery? RuntimeModules { get; init; }
    public IRuntimeJitQuery? RuntimeJit { get; init; }
}
