using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// ── Shared sub-types ─────────────────────────────────────────────────────────

public sealed record LohSegmentSnapshot(
    ulong Address,
    double FragmentationPercent,
    ulong FreeBytes,
    ulong LargestFreeBlock);

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
    IReadOnlyList<TypeSnapshot>? TopLohTypes = null) : AnalyzerDomainResult;

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
    IReadOnlyList<NameCountEntry>? TopPinnedTargetTypes = null) : AnalyzerDomainResult;

// ── LOH Fragmentation ────────────────────────────────────────────────────────

internal sealed record LohFragmentationDomainResult(
    int SegmentCount,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    int FreeBlockCount,
    double FragmentationPercent,
    ulong LargestFreeBlock,
    IReadOnlyList<LohSegmentSnapshot>? TopFragmentedSegments = null) : AnalyzerDomainResult;

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

internal sealed record LockGraphDomainResult(
    int TotalHeldLocks,
    int ContestedLockCount,
    int MaxWaitersOnSingleLock,
    int DeadlockCandidateCount,
    IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null) : AnalyzerDomainResult;
