using DumpDetective.Core.Utilities;

namespace DumpDetective.Core.Models;
internal abstract record AnalyzerDomainResult
{
    public string AnalyzerName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public IReadOnlyCollection<InsightFinding> Findings { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Metrics { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyCollection<string> Warnings { get; init; } = [];
}

internal sealed record GenericAnalyzerDomainResult : AnalyzerDomainResult;

internal sealed record TypeSnapshot(string TypeName, int Count, ulong TotalBytes, ulong LohBytes);

// Shared across multiple domain results: a name paired with a count.
internal sealed record NameCountEntry(string Name, int Count);
internal sealed record NameBytesEntry(string Name, ulong Bytes);
internal sealed record LohSegmentSnapshot(
    ulong Address,
    double FragmentationPercent,
    ulong FreeBytes,
    ulong LargestFreeBlock);

internal sealed record MemoryDomainResult(
    ulong TotalBytes,
    ulong LohBytes,
    double LohPercent,
    int TotalObjects,
    int LohObjects,
    ulong LohThresholdBytes,
    int UniqueTypes,
    IReadOnlyList<TypeSnapshot> TopTypesBySize,
    IReadOnlyList<TypeSnapshot> TopTypesByCount) : AnalyzerDomainResult;

internal sealed record GCGenerationDomainResult(
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

internal sealed record LoadedModuleSnapshot(
    string Name,
    string AssemblyName,
    string FullPath,
    ulong Address,
    ulong Size,
    bool IsDynamic);

internal sealed record ModuleConflictGroup(
    string ModuleName,
    IReadOnlyList<LoadedModuleSnapshot> Instances);

internal sealed record ModuleDomainResult(
    // Trend fields â€” IAnalyzerTrendComparer reads only these.
    int TotalModules,
    int DynamicModules,
    int UniqueModuleNames,
    int VersionConflictGroups,
    IReadOnlyList<string> ConflictingAssemblyNames,
    // Render fields â€” ModulePrinter reads these.
    IReadOnlyList<LoadedModuleSnapshot> TopModulesBySize,
    IReadOnlyList<ModuleConflictGroup> ConflictDetails) : AnalyzerDomainResult;

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

internal sealed record MemoryLeakDomainResult(
    int FinalizerQueueCount,
    int DuplicateStringPatternCount,
    ulong DuplicateStringWastedBytes,
    int TotalStrings,
    ulong TotalStringMemoryBytes,
    int UniqueStrings,
    int HighlyReferencedObjectCount,
    long SkippedReferenceAddresses,
    IReadOnlyList<NameCountEntry>? TopFinalizerTypes = null,
    IReadOnlyList<DuplicateStringSnapshot>? TopDuplicateStrings = null,
    IReadOnlyList<HighlyReferencedObjectSnapshot>? TopHighlyReferencedObjects = null) : AnalyzerDomainResult;

internal sealed record DuplicateStringSnapshot(string Preview, int Count, ulong WastedBytes);
internal sealed record HighlyReferencedObjectSnapshot(ulong Address, string TypeName, ulong Size, int IncomingReferences);

internal sealed record CollectionDomainResult(
    int TotalCollections,
    int Dictionaries,
    int Lists,
    int HashSets,
    int Queues,
    ulong TotalWastedMemory,
    int WastefulCollectionCount,
    IReadOnlyList<WastefulCollectionSnapshot>? TopWastefulCollections = null) : AnalyzerDomainResult;

internal sealed record WastefulCollectionSnapshot(
    string Type,
    int Count,
    int Capacity,
    double FillRate,
    ulong WastedMemory,
    ulong Address);

internal sealed record StaticRootDomainResult(
    int RootCount,
    ulong TotalRetainedBytes,
    IReadOnlyList<NameBytesEntry>? TopRootsByRetainedBytes = null) : AnalyzerDomainResult;

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

internal sealed record GCHandleDomainResult(
    int TotalHandles,
    int StrongLikeHandles,
    int WeakLikeHandles,
    int PinnedHandleTargets,
    IReadOnlyList<NameCountEntry>? HandlesByKind = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopPinnedTargetTypes = null) : AnalyzerDomainResult;

internal sealed record LohFragmentationDomainResult(
    int SegmentCount,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    int FreeBlockCount,
    double FragmentationPercent,
    ulong LargestFreeBlock,
    IReadOnlyList<LohSegmentSnapshot>? TopFragmentedSegments = null) : AnalyzerDomainResult;

internal sealed record DependentHandleDomainResult(
    int DependentHandleCount,
    int ResolvedEdgeCount,
    int UnresolvedTargetCount,
    double UnresolvedPercent,
    IReadOnlyList<NameCountEntry>? TopSourceTypes = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopSourceTargetEdges = null) : AnalyzerDomainResult;

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

internal sealed record LockGraphDomainResult(
    int TotalHeldLocks,
    int ContestedLockCount,
    int MaxWaitersOnSingleLock,
    int DeadlockCandidateCount,
    IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null) : AnalyzerDomainResult;
