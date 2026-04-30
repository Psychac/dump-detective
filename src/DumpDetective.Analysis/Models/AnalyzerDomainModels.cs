using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// ── Shared sub-types ─────────────────────────────────────────────────────────

public sealed record LohSegmentSnapshot(
    ulong Address,
    double FragmentationPercent,
    ulong FreeBytes,
    ulong LargestFreeBlock);

/// <summary>One bucket in the free-gap size histogram for LOH fragmentation analysis.</summary>
/// <param name="GapSizeRange">Human-readable size range, e.g. "1 KB – 64 KB".</param>
/// <param name="GapCount">Number of free gaps whose size falls within this range.</param>
public sealed record FreeGapBucket(string GapSizeRange, int GapCount);

/// <summary>Snapshot of a single large (LOH) object captured during Phase 1 index build.</summary>
public sealed record LargeObjectSnapshot(ulong Address, string TypeName, ulong Size);

/// <summary>One bucket in the object-size histogram built from <see cref="HeapIndexBuildResult.GlobalSizeBuckets"/>.</summary>
/// <param name="RangeLabel">Human-readable range label, e.g. "64–255 B".</param>
/// <param name="ObjectCount">Total number of objects whose shallow size falls in this bucket.</param>
/// <param name="TotalBytes">Sum of shallow sizes of all objects in this bucket.</param>
public sealed record SizeBucketEntry(string RangeLabel, long ObjectCount, ulong TotalBytes);

// ── Memory ───────────────────────────────────────────────────────────────────

public sealed record MemoryDomainResult(
    ulong TotalBytes,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    int LohObjects,
    ulong LohThresholdBytes,
    int UniqueTypes,
    IReadOnlyList<TypeSnapshot> TopTypesBySize,
    IReadOnlyList<TypeSnapshot> TopTypesByCount,
    IReadOnlyList<SizeBucketEntry>? SizeBucketHistogram = null) : AnalyzerDomainResult;

// ── GC Generation ────────────────────────────────────────────────────────────

/// <summary>Generation distribution for a single type, built from Phase-1 TypeAggregates.</summary>
public sealed record TypeGenerationProfile(
    string TypeName,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    int LohCount);

public sealed record GCGenerationDomainResult(
    ulong Gen0Bytes,
    int Gen0Objects,
    ulong Gen1Bytes,
    int Gen1Objects,
    ulong Gen2Bytes,
    int Gen2Objects,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    int LohObjects,
    IReadOnlyList<TypeSnapshot> TopLohTypes,
    double Gen2Pct = 0.0,
    IReadOnlyList<TypeGenerationProfile>? PerTypeGenerationProfiles = null) : AnalyzerDomainResult;

// ── Modules ──────────────────────────────────────────────────────────────────

public sealed record LoadedModuleSnapshot(
    string Name,
    string AssemblyName,
    string FullPath,
    ulong Address,
    ulong Size,
    bool IsDynamic);

public sealed record ModuleConflictGroup(
    string ModuleName,
    IReadOnlyList<LoadedModuleSnapshot> Instances);

/// <summary>Per-module heap memory and object footprint aggregated from the heap index.</summary>
public sealed record ModuleHeapStats(
    string ModuleName,
    string AssemblyName,
    int UniqueTypeCount,
    long ObjectCount,
    ulong TotalBytes);

/// <summary>Modules where memory is abnormally concentrated into very few types.</summary>
public sealed record ModuleTypeDensity(
    string ModuleName,
    string AssemblyName,
    int UniqueTypeCount,
    long ObjectCount,
    ulong TotalBytes,
    ulong BytesPerType);

internal sealed record ModuleDomainResult(
    int TotalModules,
    int DynamicModules,
    int UniqueModuleNames,
    int VersionConflictGroups,
    IReadOnlyList<string> ConflictingAssemblyNames,
    IReadOnlyList<LoadedModuleSnapshot> TopModulesBySize,
    IReadOnlyList<ModuleConflictGroup> ConflictDetails,
    IReadOnlyList<ModuleHeapStats>? TopModulesByHeapMemory = null,
    IReadOnlyList<ModuleTypeDensity>? HeavyTypeDensityModules = null) : AnalyzerDomainResult;

// ── Crash ────────────────────────────────────────────────────────────────────

internal sealed record CrashDomainResult(
    int TotalExceptions,
    int ActiveExceptions,
    IReadOnlyDictionary<string, int> ExceptionTypeCounts,
    IReadOnlyDictionary<string, int> ActiveExceptionTypeCounts,
    IReadOnlyList<CrashThreadCandidateSnapshot>? TopCrashThreadCandidates = null,
    IReadOnlyList<ExceptionInstanceSnapshot>? TopExceptionInstances = null) : AnalyzerDomainResult;

internal sealed record CrashThreadCandidateSnapshot(
    uint ThreadId,
    uint OSThreadId,
    int ActiveExceptionCount,
    string PrimaryExceptionType,
    IReadOnlyList<string> TopFrames);

internal sealed record ExceptionInstanceSnapshot(
    string Type,
    ulong Address,
    string? Message,
    int? HResult,
    string? InnerExceptionType,
    bool IsActive,
    uint? ThreadId,
    uint? OSThreadId,
    IReadOnlyList<string>? CurrentThreadFrames,
    IReadOnlyList<string>? OriginalStackTrace);

// ── Hang ─────────────────────────────────────────────────────────────────────

internal sealed record HangDomainResult(
    int TotalAliveThreads,
    int WaitingThreadCount,
    int ThreadsHoldingLocks,
    double WaitingPercent,
    IReadOnlyDictionary<string, int> WaitCategoryBreakdown,
    int TotalTaskContinuations,
    int QueuedWorkItems,
    int TotalTasks,
    int PendingTasks,
    int FaultedTasks,
    int CanceledTasks,
    bool RuntimeThreadPoolDataAvailable,
    bool TaskScanLimited,
    int HealthScore,
    IReadOnlyList<WaitingThreadSnapshot>? TopWaitingThreads = null,
    IReadOnlyList<NameCountEntry>? TopContinuationTypes = null) : AnalyzerDomainResult;

internal sealed record WaitingThreadSnapshot(
    uint ThreadId,
    uint OSThreadId,
    string WaitType,
    string WaitReason,
    int LockCount,
    string TopStackFrame);

// ── Memory Leak ───────────────────────────────────────────────────────────────

internal sealed record MemoryLeakDomainResult(
    int FinalizerQueueCount,
    int HighlyReferencedObjectCount,
    long SkippedReferenceAddresses,
    IReadOnlyList<NameCountEntry>? TopFinalizerTypes = null,
    IReadOnlyList<HighlyReferencedObjectSnapshot>? TopHighlyReferencedObjects = null) : AnalyzerDomainResult;

internal sealed record DuplicateStringSnapshot(string Preview, int Count, ulong WastedBytes);
internal sealed record HighlyReferencedObjectSnapshot(ulong Address, string TypeName, ulong Size, int IncomingReferences);

// ── String Analysis ──────────────────────────────────────────────────────────

internal sealed record LongStringEntry(ulong Address, int CharLength, ulong SizeBytes);

internal sealed record StringDomainResult(
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int DuplicatePatternCount,
    ulong DuplicateWastedBytes,
    double DuplicationRatio,
    double PctOfManagedHeap,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByWaste,
    IReadOnlyList<DuplicateStringSnapshot> TopDuplicatesByCount,
    IReadOnlyList<LongStringEntry> VeryLongStrings,
    ulong LohStringBytes,
    int InternedStringCount,
    ulong InternedStringBytes,
    int Gen2StringCount,
    ulong Gen2StringBytes) : AnalyzerDomainResult;

// ── Collections ───────────────────────────────────────────────────────────────

internal sealed record CollectionDomainResult(
    int TotalCollections,
    int Dictionaries,
    int Lists,
    int ArrayLists,
    int Stacks,
    int SortedLists,
    int SortedSets,
    int HashSets,
    int Queues,
    ulong TotalWastedMemory,
    int WastefulCollectionCount,
    IReadOnlyList<WastefulCollectionSnapshot>? TopWastefulCollections = null) : AnalyzerDomainResult;
internal sealed record WastefulCollectionSnapshot(
    string Type,
    CollectionKind Kind,
    int Count,
    int Capacity,
    double FillRate,
    ulong WastedMemory,
    ulong Address,
    int? Head = null,
    int? Tail = null,
    ulong? LargestContiguousFreeSegmentBytes = null,
    int? FreeSegmentCount = null,
    ulong ElementSize = 0,
    string ElementType = "",
    string SizeEstimateConfidence = "Unknown",
    string DetectionMethod = "",
    string? RootDescription = null);

// ── Static Roots ──────────────────────────────────────────────────────────────

internal sealed record StaticRootDomainResult(
    int RootCount,
    ulong TotalRetainedBytes,
    IReadOnlyList<NameBytesEntry>? TopRootsByRetainedBytes = null) : AnalyzerDomainResult;

// ── Reference Chains ─────────────────────────────────────────────────────────

internal sealed record ReferenceChainDomainResult(
    int AnalyzedSamples,
    int RetainedSamples,
    double RetainedPercent,
    IReadOnlyList<NameCountEntry>? TopRetainedTypes = null,
    IReadOnlyList<string>? SampleReferenceChains = null,
    IReadOnlyList<ReferenceTypeSampleSnapshot>? TopTypeSampleTraces = null) : AnalyzerDomainResult;

internal sealed record ReferenceTypeSampleSnapshot(
    string TypeName,
    int Count,
    ulong TotalSizeBytes,
    ulong? SampleAddress,
    string? SampleObjectType,
    ulong SampleObjectSize,
    bool HasGcRoot,
    string? RootPath,
    bool TraversalLimited);

// ── Threads ───────────────────────────────────────────────────────────────────

internal sealed record ThreadDomainResult(
    int TotalThreadCount,
    int AliveThreadCount,
    int InactiveThreadCount,
    int GcThreadCount,
    int BlockedThreadCount,
    int LockHoldingThreadCount,
    int ThreadsWithActiveExceptionsCount,
    int BackgroundThreadCount,
    IReadOnlyDictionary<string, int> WaitPatternBreakdown,
    IReadOnlyDictionary<string, int>? ThreadStateDistribution = null,
    IReadOnlyDictionary<string, int>? AppDomainDistribution = null,
    IReadOnlyDictionary<string, int>? GcModeDistribution = null,
    IReadOnlyList<ThreadStateSnapshot>? TopLockedThreads = null,
    IReadOnlyList<ThreadStateSnapshot>? TopBlockedThreads = null,
    IReadOnlyList<ThreadExceptionSnapshot>? ThreadsWithActiveExceptions = null,
    IReadOnlyList<NameCountEntry>? TopStackHotspots = null,
    IReadOnlyList<NameCountEntry>? TopActiveThreadHotspots = null,
    int ThreadPoolWorkerCount = 0,
    int FinalizerThreadCount = 0,
    bool FinalizerThreadBlocked = false,
    uint? FinalizerManagedThreadId = null,
    uint? FinalizerOsThreadId = null,
    int FinalizerLockCount = 0,
    IReadOnlyList<string>? FinalizerFrames = null,
    int AsyncChainThreadCount = 0,
    int MaxAsyncChainDepth = 0) : AnalyzerDomainResult;

internal sealed record ThreadStateSnapshot(
    uint ThreadId,
    uint OSThreadId,
    int LockCount,
    string ThreadState,
    string GcMode,
    string? WaitCategory,
    string? WaitReason,
    IReadOnlyList<string> TopFrames,
    int StackRootCount);

internal sealed record ThreadExceptionSnapshot(
    uint ThreadId,
    uint OSThreadId,
    string ExceptionType,
    string? ExceptionMessage,
    string ThreadState,
    string GcMode,
    int LockCount,
    IReadOnlyList<string> TopFrames,
    int StackRootCount);

// ── GC Handles ───────────────────────────────────────────────────────────────

internal sealed record GCHandleDomainResult(
    int TotalHandles,
    int StrongLikeHandles,
    int WeakLikeHandles,
    int PinnedHandleTargets,
    IReadOnlyList<NameCountEntry>? HandlesByKind = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopPinnedTargetTypes = null,
    /// <summary>Total bytes retained by all pinned GC handles (estimated from object sizes).</summary>
    ulong PinnedRetainedBytes = 0,
    /// <summary>Top pinned handle target types ranked by their total pinned bytes.</summary>
    IReadOnlyList<NameBytesEntry>? TopPinnedObjectsBySize = null) : AnalyzerDomainResult;

// ── LOH Fragmentation ────────────────────────────────────────────────────────

internal sealed record LohFragmentationDomainResult(
    int SegmentCount,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    int FreeBlockCount,
    double FragmentationPercent,
    ulong LargestFreeBlock,
    IReadOnlyList<LohSegmentSnapshot>? TopFragmentedSegments = null,
    /// <summary>Distribution of free-gap sizes across all LOH segments.</summary>
    IReadOnlyList<FreeGapBucket>? FreeGapHistogram = null,
    /// <summary>Top large objects by size (up to 20), from Phase 1 LargeObjectIndex.bin.</summary>
    IReadOnlyList<LargeObjectSnapshot>? TopLargeObjects = null) : AnalyzerDomainResult;

// ── Segments ─────────────────────────────────────────────────────────────────

internal enum HeapSegmentKind { SmallObjectHeap, LargeObjectHeap, PinnedObjectHeap, Frozen, Unknown }

internal sealed record HeapSegmentSnapshot(
    ulong Address,
    ulong Start,
    ulong End,
    ulong Length,
    ulong CommittedBytes,
    HeapSegmentKind Kind,
    int Generation,
    int ObjectCount);

internal sealed record SegmentKindSummary(
    HeapSegmentKind Kind,
    int SegmentCount,
    int ObjectCount,
    ulong TotalBytes);

internal sealed record SegmentAnalysisDomainResult(
    int TotalSegments,
    ulong TotalCommittedBytes,
    int SohSegmentCount,
    ulong SohBytes,
    int LohSegmentCount,
    ulong LohBytes,
    int PohSegmentCount,
    ulong PohBytes,
    int FrozenSegmentCount,
    ulong FrozenBytes,
    double LohPercent,
    double PohPercent,
    IReadOnlyList<SegmentKindSummary> KindSummaries,
    IReadOnlyList<HeapSegmentSnapshot>? TopSegmentsBySize = null) : AnalyzerDomainResult;

// ── Dependent Handles ────────────────────────────────────────────────────────

internal sealed record DependentHandleDomainResult(
    int DependentHandleCount,
    int ResolvedEdgeCount,
    int UnresolvedTargetCount,
    double UnresolvedPercent,
    IReadOnlyList<NameCountEntry>? TopSourceTypes = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopSourceTargetEdges = null) : AnalyzerDomainResult;

// ── Thread Stack Clusters ────────────────────────────────────────────────────

internal sealed record ThreadStackClusterDomainResult(
    int AliveThreadCount,
    int UniqueClusters,
    int SingletonSignatures,
    double DiversityPercent,
    IReadOnlyList<string> TopClusterSignatures,
    IReadOnlyList<ThreadClusterSnapshot>? TopClusters = null) : AnalyzerDomainResult;

internal sealed record ThreadClusterSnapshot(
    int Count,
    IReadOnlyList<uint> SampleOsThreadIds,
    string Signature);

// ── Event Leaks ──────────────────────────────────────────────────────────────

internal sealed record EventLeakDomainResult(
    int TotalEventLeakInstances,
    int TotalSubscribers,
    int StaticEventLeakCount,
    int InstanceEventLeakCount,
    IReadOnlyList<NameCountEntry>? TopPublisherEventsBySubscribers = null,
    IReadOnlyList<EventLeakGroupSnapshot>? TopLeakGroups = null,
    IReadOnlyList<EventLeakInstanceSnapshot>? TopLeakInstances = null) : AnalyzerDomainResult;

internal sealed record EventLeakGroupSnapshot(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    int SeverityScore,
    int InstanceCount,
    int TotalSubscribers,
    double AverageSubscribers,
    int MinSubscribers,
    int MaxSubscribers,
    IReadOnlyList<NameCountEntry>? TopSubscriberTypes = null);

internal sealed record EventLeakInstanceSnapshot(
    string PublisherType,
    string EventFieldName,
    bool IsStatic,
    ulong PublisherAddress,
    int SeverityScore,
    int SubscriberCount,
    string? RootHint,
    IReadOnlyList<string>? SubscriberTypes = null);

// ── Async Task ───────────────────────────────────────────────────────────────

internal sealed record OrphanedTaskSnapshot(ulong Address, string TaskType, string? ResultType, ulong Size);

internal sealed record AsyncTaskDomainResult(
    int TotalTasks,
    int PendingTasks,
    int RunningTasks,
    int FaultedTasks,
    int CanceledTasks,
    int CompletedTasks,
    int OrphanedTasks,
    int MaxContinuationDepth,
    double AvgContinuationDepth,
    bool TaskScanLimited,
    IReadOnlyList<NameCountEntry> TopPendingTaskTypes,
    IReadOnlyList<NameCountEntry> TopFaultedTaskTypes,
    IReadOnlyList<NameCountEntry> TopContinuationTypes,
    IReadOnlyList<OrphanedTaskSnapshot> TopOrphanedTasks) : AnalyzerDomainResult;

// ── Lock Graph ────────────────────────────────────────────────────────────────

internal sealed record DeadlockCandidateSnapshot(
    uint ManagedThreadId,
    uint OsThreadId,
    IReadOnlyList<string> LockObjectTypes,
    string CycleSummary);

internal sealed record ContestedLockSnapshot(
    ulong ObjectAddress,
    string ObjectTypeName,
    int WaitingThreadCount,
    uint? OwnerManagedThreadId,
    int RecursionCount);

internal sealed record LockGraphDomainResult(
    int TotalHeldLocks,
    int ContestedLockCount,
    int MaxWaitersOnSingleLock,
    int DeadlockCandidateCount,
    IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null,
    IReadOnlyList<DeadlockCandidateSnapshot>? DeadlockCandidateDetails = null,
    IReadOnlyList<ContestedLockSnapshot>? ContestedLockDetails = null) : AnalyzerDomainResult;

// ── Allocation Pattern ────────────────────────────────────────────────────────

public enum AllocationProfile { Transient, Steady, Retained, Mixed }
public enum GCPressureLevel   { Low, Moderate, High, Critical }

public sealed record TypeAllocationProfile(
    string TypeName,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    double LongLivedRatio,
    AllocationProfile Profile);

internal sealed record AllocationPatternDomainResult(
    // Object-count percentages (objects in generation / total objects)
    double Gen0CountPct,
    double Gen1CountPct,
    double Gen2CountPct,
    double LohCountPct,
    // Byte-size percentages (bytes in generation / total managed bytes)
    double Gen0SizePct,
    double Gen1SizePct,
    double Gen2SizePct,
    double LohSizePct,
    AllocationProfile Profile,
    GCPressureLevel GCPressure,
    double PromotionPressureScore,
    IReadOnlyList<TypeAllocationProfile> TopShortLivedTypes,
    IReadOnlyList<TypeAllocationProfile> TopLongLivedTypes) : AnalyzerDomainResult;

// ── ObjectShapeAnalyzer ──────────────────────────────────────────────────────────

public enum ObjectShapeCategory
{
    ReferenceHeavy,   // refRatio > 0.6
    ValueHeavy,       // refRatio < 0.2  (and totalFields > 0)
    Balanced,         // 0.2 – 0.6
    Scalar,           // 0 fields (primitives / no-field types)
}

public sealed record TypeShapeProfile(
    string TypeName,
    int TotalFields,
    int ReferenceFields,
    int ValueFields,
    double ReferenceFieldRatio,
    ulong InstanceCount,
    bool IsFinalizable,
    bool IsValueType,
    int BaseTypeChainDepth,
    int InterfaceCount,
    ObjectShapeCategory Category);

internal sealed record ObjectShapeAnalyzerDomainResult(
    IReadOnlyList<TypeShapeProfile> TopReferenceHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopValueHeavyTypes,
    int TotalTypesAnalyzed,
    double AvgRefFieldsPerType) : AnalyzerDomainResult;

// ── GCRootAnalyzer domain models ──────────────────────────────────────────────

public sealed record RootKindSummary(
    string Kind,
    int Count,
    ulong EstimatedRetainedBytes,
    double PctOfManagedHeap);

public sealed record RootFinding(
    string RootKind,
    ulong RootAddress,
    string? FieldDescription,
    string TargetTypeName,
    ulong TargetAddress,
    ulong EstimatedRetainedBytes,
    int SeverityScore);

public sealed record RootPathFinding(
    ulong TargetAddress,
    string TargetTypeName,
    string RootKind,
    IReadOnlyList<string> PathTypeNames,
    int PathLength,
    bool WasCapped);

internal sealed record GCRootDomainResult(
    int TotalRoots,
    IReadOnlyList<RootKindSummary> ByKind,
    IReadOnlyList<RootFinding> TopRootsBySeverity,
    IReadOnlyList<RootPathFinding> RootPaths,
    bool PathSearchCapped,
    int PathSearchCappedCount) : AnalyzerDomainResult;
