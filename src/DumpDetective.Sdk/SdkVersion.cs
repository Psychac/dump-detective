namespace DumpDetective.Sdk;

/// <summary>
/// Version of the source-neutral SDK contract surface (identity, capability, observation,
/// synthesis). Independent of the product's own version — plugins and schema files pin against
/// this, not against a product release. See docs/refactor/modularity/phase-1-contracts-sdk.md.
/// </summary>
/// <remarks>
/// <b>Bump policy, decided 2026-09-10:</b> <see cref="Minor"/> bumps once per fix that changes the
/// SDK's public API surface (added/removed/renamed member, changed signature, changed documented
/// behavior a caller could depend on) — including additive, non-breaking changes, not just breaking
/// ones; <see cref="Major"/> is reserved for an actual breaking change once something real depends
/// on this assembly. Bump in the same commit as the fix, not batched later — this field went
/// untouched across two whole sessions of API changes (docs/refactor/modularity/phase-1-sdk-review-findings.md
/// item 10) specifically because "bump it eventually" never has a forcing function. This is that
/// forcing function.
/// </remarks>
public static class SdkVersion
{
    public const int Major = 0;

    /// <summary>22 as of 2026-10-12 (<see cref="Artifacts.CoverageBoundary.Affected"/> became nullable, for a limit on a scan as a whole with no count — M3 step 5).
    /// Before that, 21 as of 2026-10-12 (added <see cref="Artifacts.CoverageStatus.NotSupplied"/> — a missing capability whose whole family no
    /// artifact in the session provides, so a dump-only run does not read as a gap in the dump; M3 step 5).
    /// Before that, 20 as of 2026-10-12 (<see cref="Artifacts.CoverageRecord.Capability"/> became nullable and
    /// <see cref="Artifacts.CoverageImpactKind.Failed"/> was added, for a failed analyzer, which belongs to no capability;
    /// <see cref="Synthesis.SynthesisContext.AnalyzerRunSummary"/> was replaced by <see cref="Synthesis.SynthesisContext.FailedAnalyzers"/>,
    /// the names the one rule that read the counts needs as coverage — M3 step 4).
    /// Before that, 19 as of 2026-10-12 (added <see cref="Synthesis.ICoverageRule"/>, <see cref="Artifacts.CoverageBoundary"/>,
    /// <see cref="Artifacts.CoverageImpactKind.Limited"/> and <see cref="Artifacts.CoverageRecord"/>'s <c>Boundary</c> / <c>Rule</c> / <c>Claims</c> —
    /// M3 step 4 of docs/ReportStructure/m3-coverage-plan.md).
    /// Before that, 18 as of 2026-10-12 (added <see cref="Artifacts.CoverageRecord"/>, <see cref="Artifacts.CoverageStatus"/>,
    /// <see cref="Artifacts.CoverageImpact"/> and <see cref="Artifacts.CoverageImpactKind"/> — a session's per-capability
    /// coverage, M3 step 2 of docs/ReportStructure/m3-coverage-plan.md; no <c>capability-registry.json</c> bump, no new capability).
    /// Before that, 17 as of 2026-09-13 (added <see cref="Analysis.RuntimeThreadRef.ExceptionAddress"/> —
    /// correlates a thread's live current exception back to the specific heap-scanned instance by
    /// address, which <c>HasCurrentException</c>/<c>ExceptionTypeName</c>/<c>ExceptionMessage</c>
    /// alone can't do — and <see cref="Analysis.IDumpObjectFieldQuery.EnumerateExceptionStackTrace"/>,
    /// a Tier-2 primitive delegating to <c>ClrObject.AsException()?.EnumerateExceptionStackTrace()</c>
    /// for the one piece of <c>ClrException</c> genuinely not equivalent to a plain field read
    /// (confirmed by decompiling the installed ClrMD package: <c>Message</c>/<c>HResult</c>/<c>Inner</c>
    /// are exactly <c>"_message"</c>/<c>"_HResult"</c>/<c>"_innerException"</c> field reads under the
    /// hood, but <c>StackTrace</c> walks the exception's internal binary trace-info array). Both found
    /// necessary by <c>CrashAnalyzer</c>'s retyping. No <c>capability-registry.json</c> bump — both
    /// extend already-registered capabilities (<c>runtime.threads</c>, <c>dump.object-fields</c>).
    /// Before that, 16 as of 2026-09-12 (added <see cref="Analysis.IDumpObjectFieldQuery.EnumerateInstanceFields"/>/
    /// <see cref="Analysis.InstanceFieldDescriptor"/> — per-type field name+kind discovery, without a
    /// real <c>ClrType</c>; and <see cref="Analysis.IHeapReverseReferenceQuery.TryGetReverseReferences"/>
    /// — a truncation-aware parent lookup, <c>heap.reverse-references</c>'s first real dump-side
    /// implementation. Both found necessary by <c>CollectionAnalyzer</c>'s retyping. A third addition,
    /// an <c>IHeapRootPathQuery.TryFindRootPath</c> overload accepting a caller-supplied force-expand
    /// predicate, was built and then removed the same session once it hung a real-dump run
    /// indefinitely — see that interface's own remarks; no version bump owed for a change that never
    /// shipped. No <c>capability-registry.json</c> bump — both shipped primitives extend
    /// already-registered capabilities (<c>dump.object-fields</c>, <c>heap.reverse-references</c>)
    /// rather than introducing new vocabulary.
    /// Before that, 15 as of 2026-09-12 (added <see cref="Analysis.IDumpObjectFieldQuery.TryReadNestedStructField"/>
    /// — one level of embedded-struct field read, needed for
    /// <c>ManualResetValueTaskSourceCore&lt;TResult&gt;._completed</c> — and
    /// <see cref="Analysis.IDumpArrayShapeQuery.EnumerateObjectReferenceElements"/> — array element
    /// *address* enumeration, replacing a hand-rolled <c>List&lt;T&gt;._items</c>/<c>_size</c> walk —
    /// plus <see cref="Analysis.HeapTypeStatistics"/>'s <c>IsTaskType</c>/<c>IsTaskCompletionSourceType</c>/
    /// <c>ImplementsValueTaskSource</c>/<c>ValueTaskSourceCoreFieldName</c>. All found necessary by
    /// <c>AsyncTaskAnalyzer</c>'s retyping. No <c>capability-registry.json</c> bump — both new
    /// primitives extend already-registered capabilities (<c>dump.object-fields</c>,
    /// <c>dump.array-elements</c>) rather than introducing new vocabulary.
    /// Before that, 14 as of 2026-09-12 (added <see cref="Analysis.IDumpObjectFieldQuery.TryGetFieldTypeName"/>
    /// and <see cref="Analysis.IDumpObjectFieldQuery.EnumerateObjectReferenceFields"/> — two Tier-2
    /// <c>dump.object-fields</c> primitives closing the field-*shape*-enumeration gap
    /// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md flagged as still open, plus
    /// <see cref="Analysis.HeapTypeStatistics"/>'s <c>IsAsyncStateMachineType</c> — all found necessary
    /// by <c>AsyncStateMachineAnalyzer</c>'s retyping.
    /// Before that, 13 as of 2026-09-12 (added <see cref="Analysis.IRuntimeThreadPoolQuery"/>,
    /// <see cref="Analysis.RuntimeThreadPoolInfo"/>, <c>CapabilityVocabulary.RuntimeThreadPool</c>,
    /// and <c>Analysis.AnalysisContext.RuntimeThreadPool</c> — the <c>runtime.thread-pool</c>
    /// capability built for <c>HangAnalyzer</c>'s retyping, its first real consumer).
    /// Before that, 12 as of 2026-09-12 (added <see cref="Analysis.RuntimeThreadRef"/>'s <c>IsBackground</c>/
    /// <c>ThreadStateDisplay</c>/<c>GcModeDisplay</c>/<c>AppDomainName</c>/<c>HasCurrentException</c>/
    /// <c>ExceptionTypeName</c>/<c>ExceptionMessage</c>/<c>StackSizeBytes</c> — found necessary by
    /// <c>ThreadAnalyzer</c>'s retyping; all Tier-1 despite the exception fields' name, since they
    /// come from ClrMD's own <c>ClrException.Type</c>/<c>Message</c> convenience properties, not a
    /// raw field read).
    /// Before that, 11 as of 2026-09-12 (added <see cref="Analysis.IHeapReachabilityQuery"/>'s dump-side
    /// implementation and <c>Analysis.AnalysisContext.HeapReachability</c> wiring, plus
    /// <see cref="Analysis.FieldValueKind"/>'s <c>NativeInt</c> case and
    /// <c>FieldValue.ForNativeInt</c>/<c>AsNativeInt</c> — found necessary by
    /// <c>WeakReferenceAnalyzer</c>'s retyping. Also covers the untracked bump owed from
    /// <c>FinalizableObjectAnalyzer</c>'s retyping the same day (<see cref="Analysis.IHeapFinalizerQueueQuery"/>'s
    /// redesign from raw addresses to <c>FinalizerQueueObjectRef</c>) — missed at the time, folded in
    /// here rather than issuing a separate bump for a change already shipped, same precedent
    /// <c>ObjectShapeAnalyzer</c>'s retyping already set for this exact situation.
    /// Before that, 10 as of 2026-09-12 (added <see cref="Analysis.HeapTypeStatistics"/>'s <c>IsEnum</c>/
    /// <c>StaticSize</c>/<c>BaseTypeName</c>/<c>HasIEquatableInterface</c>/<c>DeclaredFieldByteSize</c>
    /// — found necessary by <c>BoxingAnalyzer</c>'s retyping, which turned out Tier-1-only on
    /// inspection like <c>ObjectShapeAnalyzer</c> before it: no per-instance field *value* reads, only
    /// per-type structural metadata already isomorphic to what <c>IHeapTypeStatisticsQuery</c> exposes).
    /// Before that, 9 as of 2026-09-12 (added <see cref="Analysis.FieldValueKind"/>'s <c>UInt32</c> case
    /// and <c>FieldValue.ForUInt32</c>/<c>AsUInt32</c> — found necessary by <c>TimerLeakAnalyzer</c>'s
    /// retyping: <c>TimerQueueTimer._period</c>/<c>m_period</c> is a genuine <c>uint</c> field whose
    /// <c>uint.MaxValue</c> "infinite" sentinel reinterprets incorrectly through <c>Int32</c>'s signed
    /// semantics).
    /// Before that, 8 as of 2026-09-11 (added <see cref="Analysis.IHeapRootPathQuery"/> and
    /// <see cref="Analysis.RootPathSearchOptions"/>, plus <c>CapabilityVocabulary.HeapRootPaths</c>
    /// and <c>Analysis.AnalysisContext.HeapRootPaths</c> — the <c>heap.root-paths</c> capability
    /// built for <c>DbConnectionAnalyzer</c>/<c>TimerLeakAnalyzer</c>'s retyping).
    /// Before that, 7 as of 2026-09-11 (added <see cref="Analysis.IDumpObjectFieldQuery.TryGetStringValue"/>
    /// — a second Tier-2 <c>dump.object-fields</c> primitive for "interpret this address as a string
    /// directly," found necessary by <c>HttpObjectAnalyzer</c>'s retyping: several field-chase chains
    /// call <c>ClrObject.AsString()</c> on a field's object-reference *result*, not on a named
    /// string-typed field, which <c>TryReadField</c> alone can't express).
    /// Before that, 6 as of 2026-09-11 (added <see cref="Analysis.IDumpArrayShapeQuery"/>,
    /// <c>CapabilityVocabulary.DumpArrayElements</c>, <c>Analysis.AnalysisContext.DumpArrayShapes</c>,
    /// and <see cref="Analysis.HeapTypeStatistics"/>'s <c>ArrayElementTypeName</c>/<c>ArrayRank</c>/
    /// <c>ArrayElementIsObjectReference</c>/<c>ArrayElementIsSupportedNumeric</c>/<c>ArrayElementSizeBytes</c>
    /// fields — <c>ArrayAnalyzer</c>'s retyping, the Tier-2 wave's second new capability). Also
    /// covers the untracked bump this field owed from <c>ObjectShapeAnalyzer</c>'s retyping earlier
    /// the same session (<c>HeapTypeStatistics.TotalFields</c>/<c>ReferenceFields</c>/<c>ValueFields</c>/
    /// <c>IsValueType</c>/<c>IsArray</c>/<c>BaseTypeChainDepth</c>/<c>InterfaceCount</c> and
    /// <c>IHeapTypeStatisticsQuery.HasTypeShapeData</c>) — missed at the time, folded in here rather
    /// than issuing a separate bump for a change already shipped.
    /// Before that, 5 as of 2026-09-11 (added <see cref="Analysis.IDumpObjectFieldQuery"/>,
    /// <see cref="Analysis.FieldValue"/>, <c>CapabilityVocabulary.DumpObjectFields</c>, and
    /// <c>Analysis.AnalysisContext.DumpObjectFields</c> — the Tier-2 pilot's new capability surface).
    /// Before that, 4 as of 2026-09-10 (removed <c>CapabilityVocabulary.TemporalSeries</c>, follow-on
    /// to finding 11's <c>TemporalKind.Series</c> removal) — bumped from 3 (finding 11 itself). Before
    /// that, one catch-up bump (0.1 → 0.2) covered every SDK API change already made this session
    /// before the per-finding policy above existed (the `Analysis/` namespace, capability
    /// additions/splits, the `EntityRef`/`IHeapDominatorQuery`/positional-record/JSON-converter
    /// fixes) — not a precise per-change reconstruction, since nothing yet depends on intermediate
    /// values enough to make that worth simulating.</summary>
    public const int Minor = 22;

    public static string AsString => $"{Major}.{Minor}";
}
