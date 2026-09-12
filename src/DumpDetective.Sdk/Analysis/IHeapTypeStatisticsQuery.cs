using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>Aggregate per-type facts — mirrors <c>IHeapAnalysisCache.GetOrBuildTypeStatistics</c>'s
/// <c>CachedTypeStatistics</c> shape, source-neutral, extended 2026-09-10 with the per-generation
/// fields <c>GCGenerationAnalyzer</c> (the Phase 1 retyping pilot) measurably needs — see
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md. These mirror the dump-side
/// <c>TypeAggregateIndexEntry</c> one-for-one; <see cref="Gen0Count"/>/<see cref="Gen1Count"/>/
/// <see cref="Gen2Count"/>/<see cref="Gen2TotalSize"/>/<see cref="IsFinalizableType"/> are all zero/
/// false when <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/> is <c>false</c> — the
/// dump-side cache's coarser fallback statistics carry no per-generation breakdown at all.</summary>
public readonly record struct HeapTypeStatistics(
    TypeRef Type,
    long InstanceCount,
    ulong TotalSize,
    long LohCount,
    ulong LohSize,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    ulong Gen2TotalSize,
    bool IsFinalizableType,
    /// <summary>
    /// A representative live instance's address for this type, or 0 when none is known. Added
    /// 2026-09-11 for <c>MemoryAnalyzer</c>'s retyping — mirrors
    /// <c>IHeapAnalysisCache.GetSampleInstanceAddress</c>/<c>TypeAggregateIndexEntry.SampleAddress</c>,
    /// carried alongside the rest of this type's stats rather than as a separate by-name lookup
    /// method (which would either repeat this interface's per-type dictionary scan per call, or
    /// duplicate one internally) — a caller who already has a <see cref="HeapTypeStatistics"/> in
    /// hand never needs a second round trip.
    /// </summary>
    ulong SampleAddress = 0,
    /// <summary>
    /// The declaring module's file name, or an empty string when unresolved — mirrors
    /// <c>CachedTypeStatistics.ModuleName</c>. Deliberately not <c>string?</c>: the dump-side cache's
    /// own field is never null, only sometimes empty, and this preserves that exactly rather than
    /// introducing a normalization this interface doesn't need to make (callers that want `null` for
    /// "unresolved" — e.g. a report field — normalize at that point, same as the pre-retyping analyzer
    /// already did).
    /// </summary>
    string ModuleName = "",
    /// <summary>
    /// Declared instance field count, split reference vs. value — mirrors <c>TypeShapeEntry.TotalFields</c>/
    /// <c>RefFields</c>/<c>ValFields</c>. Added 2026-09-11 for <c>ObjectShapeAnalyzer</c>'s retyping.
    /// All three are 0 when <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/> is <c>false</c> —
    /// a genuinely different gate from <see cref="IHeapTypeStatisticsQuery.HasExactGenerationData"/>,
    /// since the dump-side <c>TypeShapeCache</c> can be absent even when the rest of the per-type
    /// generation index is present (e.g. an older on-disk cache format).
    /// </summary>
    int TotalFields = 0,
    int ReferenceFields = 0,
    int ValueFields = 0,
    /// <summary>Mirrors <c>ClrType.IsValueType</c>/<c>IsArray</c> — always available regardless of
    /// <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/>, since both come from the type itself,
    /// not the shape cache.</summary>
    bool IsValueType = false,
    bool IsArray = false,
    /// <summary>Depth of the <c>BaseType</c> chain, capped at 20 (mirrors the pre-retyping analyzer's
    /// own cap) — 0 when <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/> is <c>false</c>.</summary>
    int BaseTypeChainDepth = 0,
    /// <summary>Count from <c>ClrType.EnumerateInterfaces()</c>; 0 on enumeration failure (a rare,
    /// defensive fallback the pre-retyping analyzer logged and this one silently accepts — see that
    /// type's own remarks) as well as when <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/> is
    /// <c>false</c>.</summary>
    int InterfaceCount = 0,
    /// <summary>
    /// Array-shape facts, meaningful only when <see cref="IsArray"/> is <c>true</c> (default/empty
    /// otherwise) — added 2026-09-11 for <c>ArrayAnalyzer</c>'s retyping. Come from <c>ClrType.ComponentType</c>
    /// directly, so available regardless of <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/>,
    /// same as <see cref="IsValueType"/>/<see cref="IsArray"/> themselves.
    /// <see cref="ArrayElementTypeName"/> is the resolved component type's display name (e.g.
    /// <c>"Int32"</c>, <c>"MyClass"</c>); <see cref="ArrayRank"/> is parsed from the array type's own
    /// name (commas between the brackets), matching the pre-retyping analyzer's own detection
    /// exactly; <see cref="ArrayElementIsObjectReference"/>/<see cref="ArrayElementIsSupportedNumeric"/>/
    /// <see cref="ArrayElementSizeBytes"/> drive which sparse-walk strategy
    /// <c>IDumpArrayShapeQuery.CountNullOrZeroElements</c>'s dump-side implementation picks and how
    /// wasted bytes are computed from a null/zero count — mirroring the pre-retyping analyzer's own
    /// <c>IsSupportedNumericElementType</c>/<c>ElementSizeBytes</c> switches, moved here so the SDK
    /// consumer never needs to know ClrMD's <c>ClrElementType</c> at all.
    /// </summary>
    string ArrayElementTypeName = "",
    int ArrayRank = 0,
    bool ArrayElementIsObjectReference = false,
    bool ArrayElementIsSupportedNumeric = false,
    int ArrayElementSizeBytes = 0,
    /// <summary>
    /// Mirrors <c>ClrType.IsEnum</c>/<c>StaticSize</c>/<c>BaseType.Name</c> — added 2026-09-12 for
    /// <c>BoxingAnalyzer</c>'s retyping. Always available regardless of
    /// <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/>, same as <see cref="IsValueType"/>/
    /// <see cref="IsArray"/> — all three come from the type itself, not the shape cache.
    /// <see cref="BaseTypeName"/> is an empty string when the type has no base type (e.g. <c>object</c>
    /// itself), preserving the pre-retyping analyzer's own defensive
    /// <c>BaseType?.Name == "System.ValueType"/"System.Enum"</c> boxing-detection fallback exactly.
    /// </summary>
    bool IsEnum = false,
    int StaticSize = 0,
    string BaseTypeName = "",
    /// <summary>
    /// <c>true</c> when <c>ClrType.EnumerateInterfaces()</c> contains an <c>IEquatable&lt;T&gt;</c>
    /// implementation; <c>false</c> on enumeration failure, same defensive fallback as
    /// <see cref="InterfaceCount"/> — but computed independently of
    /// <see cref="IHeapTypeStatisticsQuery.HasTypeShapeData"/>, since the pre-retyping
    /// <c>BoxingAnalyzer</c> always computed this from <c>TypeAggregates</c> alone, with no shape-cache
    /// dependency at all. <c>false</c> for reference types (never computed for them at all) — this
    /// field is only ever meaningful for a boxed value type, and walking interface metadata for every
    /// reference type in a large dump's full type population measurably cost minutes for zero benefit.
    /// </summary>
    bool HasIEquatableInterface = false,
    /// <summary>
    /// Sum of <c>ClrInstanceField.Size</c> across the type's own declared instance fields — used to
    /// detect struct padding waste (<c>StaticSize - DeclaredFieldByteSize</c>). 0 on any field-read
    /// failure (ClrMD field APIs can fail on corrupt dumps, mirroring the pre-retyping analyzer's own
    /// defensive catch), when field data is genuinely unavailable, or for a reference type — same
    /// value-types-only gate as <see cref="HasIEquatableInterface"/>, for the same measured reason.
    /// </summary>
    int DeclaredFieldByteSize = 0,
    /// <summary>
    /// <c>true</c> when this type's name matches the compiler-generated async state machine
    /// pattern (<c>&lt;MethodName&gt;d__N</c>) — mirrors <c>TypeAggregateFlags.IsAsyncStateMachineType</c>.
    /// Added 2026-09-12 for <c>AsyncStateMachineAnalyzer</c>'s retyping. Unlike
    /// <see cref="HasIEquatableInterface"/>/<see cref="DeclaredFieldByteSize"/>, this is populated on
    /// <em>both</em> the index-backed and no-index-fallback paths (same treatment as
    /// <see cref="IsFinalizableType"/>) — a plain name-pattern check, no ClrMD field/interface
    /// metadata walk, so there's no cost reason to gate it behind an index.
    /// </summary>
    bool IsAsyncStateMachineType = false,
    /// <summary>
    /// <c>true</c> when the type's name matches <c>TypeAggregateFlags.IsTaskType</c>'s pattern (a
    /// genuine <c>System.Threading.Tasks.Task</c>/<c>Task&lt;T&gt;</c>/internal promise type, not a
    /// <c>TaskCompletionSource</c> that merely shares the name prefix). Two-path like
    /// <see cref="IsAsyncStateMachineType"/>: index-backed via <c>TypeAggregateFlags.IsTaskType</c>,
    /// no-index fallback via <c>TaskTypeNamePattern.IsTaskType</c>. Added 2026-09-12 for
    /// <c>AsyncTaskAnalyzer</c>'s retyping.
    /// </summary>
    bool IsTaskType = false,
    /// <summary>
    /// <c>true</c> for <c>TaskCompletionSource</c>/<c>TaskCompletionSource&lt;T&gt;</c>. No
    /// <c>TypeAggregateFlags</c> bit exists for this — always computed from the type name (pure
    /// string check via <c>TaskTypeNamePattern.IsTaskCompletionSource</c>), so unlike
    /// <see cref="IsTaskType"/> this is populated identically on both the index-backed and
    /// no-index-fallback paths. Added 2026-09-12 for <c>AsyncTaskAnalyzer</c>'s retyping.
    /// </summary>
    bool IsTaskCompletionSourceType = false,
    /// <summary>
    /// <c>true</c> when the type implements <c>IValueTaskSource</c>/<c>IValueTaskSource&lt;T&gt;</c>
    /// via an embedded <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> field. Structural (needs
    /// a real <c>ClrType</c>, not a name check), so — unlike <see cref="IsTaskType"/>/
    /// <see cref="IsTaskCompletionSourceType"/> — computed once per distinct type only, mirroring
    /// this record's own <see cref="HasIEquatableInterface"/> cost shape. Added 2026-09-12 for
    /// <c>AsyncTaskAnalyzer</c>'s retyping (ported from <c>ValueTaskSourcePattern</c>).
    /// </summary>
    bool ImplementsValueTaskSource = false,
    /// <summary>Name of the embedded <c>ManualResetValueTaskSourceCore&lt;TResult&gt;</c> field when
    /// <see cref="ImplementsValueTaskSource"/> is <c>true</c>; <c>null</c> otherwise.</summary>
    string? ValueTaskSourceCoreFieldName = null);

/// <summary>The <c>heap.types</c> capability's aggregate-statistics surface (distinct
/// <see cref="IHeapObjectStream"/>/<see cref="IHeapObjectLookup"/>, per-object).</summary>
public interface IHeapTypeStatisticsQuery
{
    IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics();

    /// <summary>
    /// <c>true</c> when <see cref="GetTypeStatistics"/> is backed by the dump's prebuilt per-type
    /// generation index (real <see cref="HeapTypeStatistics.Gen0Count"/>/<see cref="HeapTypeStatistics.Gen1Count"/>/
    /// <see cref="HeapTypeStatistics.Gen2Count"/>/<see cref="HeapTypeStatistics.Gen2TotalSize"/>/
    /// <see cref="HeapTypeStatistics.IsFinalizableType"/> data); <c>false</c> when it falls back to
    /// coarser statistics with no generation breakdown (every one of those fields reads as its
    /// default). Mirrors the dump-side <c>HeapAnalysisCache.TryGetHeapIndex</c>-vs-
    /// <c>GetOrBuildTypeStatistics</c> fork — callers that care about generation fidelity (rather
    /// than just count/size) should check this before trusting the generation fields.
    /// </summary>
    bool HasExactGenerationData { get; }

    /// <summary>
    /// <c>true</c> when <see cref="GetTypeStatistics"/>'s <see cref="HeapTypeStatistics.TotalFields"/>/
    /// <see cref="HeapTypeStatistics.ReferenceFields"/>/<see cref="HeapTypeStatistics.ValueFields"/>/
    /// <see cref="HeapTypeStatistics.BaseTypeChainDepth"/>/<see cref="HeapTypeStatistics.InterfaceCount"/>
    /// carry real data (backed by the dump's prebuilt per-type shape cache); <c>false</c> when no
    /// shape cache was built, in which case every one of those fields reads as its default. Added
    /// 2026-09-11 for <c>ObjectShapeAnalyzer</c>'s retyping — deliberately not folded into
    /// <see cref="HasExactGenerationData"/>, since the dump-side shape cache and generation index are
    /// built and can go missing independently of each other.
    /// </summary>
    bool HasTypeShapeData { get; }

    /// <summary>
    /// Exact per-generation byte totals for the small object heap (excludes LOH/POH), computed
    /// directly from segment sub-ranges — always available regardless of
    /// <see cref="HasExactGenerationData"/>, since it needs no per-type index at all.
    /// </summary>
    (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals();

    /// <summary>
    /// Exact total live object count from the Phase 1 scan, or <c>null</c> when
    /// <see cref="HasExactGenerationData"/> is <c>false</c> (no index). Added 2026-09-11 for
    /// <c>HeapTopologyAnalyzer</c>'s retyping — distinct from summing
    /// <see cref="HeapTypeStatistics.InstanceCount"/> across <see cref="GetTypeStatistics"/>, which
    /// can undercount: that dictionary is keyed by resolved type <em>name</em>, so two distinct
    /// method tables resolving to the same name collapse to one entry (documented, accepted gap on
    /// the dump-side implementation); this property is not derived from that dictionary.
    /// </summary>
    long? ExactObjectCount { get; }

    /// <summary>
    /// Sum of <see cref="HeapTypeStatistics.TotalSize"/> across every indexed type, or 0 when
    /// <see cref="HasExactGenerationData"/> is <c>false</c>. Added 2026-09-11 for
    /// <c>HeapTopologyAnalyzer</c>'s retyping — computed directly from the index on the dump-side
    /// implementation, not by summing <see cref="GetTypeStatistics"/>'s result, for the same
    /// name-collision reason as <see cref="ExactObjectCount"/>.
    /// </summary>
    ulong GetTotalIndexedBytes();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetDistinctMethodTables</c> — type
    /// least one live instance, without full object-index scan. <c>null</c> unavailable.</summary>
    IReadOnlyList<TypeRef>? TryGetDistinctTypes();

    /// <summary>Mirrors <c>IHeapAnalysisCache.TryGetGlobalSizeBuckets</c> — method
    /// raw <c>long[]?</c>, but every other SDK collection type `IReadOnly*`,
    /// `IReadOnlyList` instead reproducing encapsulation leak; see
    /// docs/refactor/modularity/phase-1-sdk-review-findings.md item 6. <c>null</c>
    /// unavailable.</summary>
    IReadOnlyList<long>? TryGetGlobalSizeBuckets();
}
