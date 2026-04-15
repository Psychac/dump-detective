using DumpDetective.Utilities;

namespace DumpDetective.Models
{
    internal abstract record AnalyzerDomainResult;

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
        int UniqueTypes,
        IReadOnlyList<TypeSnapshot> TopTypesBySize,
        IReadOnlyList<TypeSnapshot> TopTypesByCount) : AnalyzerDomainResult;

    internal sealed record GCGenerationDomainResult(
        ulong Gen2Bytes,
        ulong LohBytes,
        double LohPercent,
        int TotalObjects,
        int LohObjects) : AnalyzerDomainResult;

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
        // Trend fields — IAnalyzerTrendComparer reads only these.
        int TotalModules,
        int DynamicModules,
        int UniqueModuleNames,
        int VersionConflictGroups,
        IReadOnlyList<string> ConflictingAssemblyNames,
        // Render fields — ModulePrinter reads these.
        IReadOnlyList<LoadedModuleSnapshot> TopModulesBySize,
        IReadOnlyList<ModuleConflictGroup> ConflictDetails) : AnalyzerDomainResult;

    internal sealed record CrashDomainResult(
        int TotalExceptions,
        int ActiveExceptions,
        IReadOnlyDictionary<string, int> ExceptionTypeCounts,
        IReadOnlyDictionary<string, int> ActiveExceptionTypeCounts) : AnalyzerDomainResult;

    internal sealed record HangDomainResult(
        int TotalAliveThreads,
        int WaitingThreadCount,
        double WaitingPercent,
        IReadOnlyDictionary<string, int> WaitCategoryBreakdown,
        int QueuedWorkItems,
        int PendingTasks,
        int FaultedTasks,
        int CanceledTasks,
        int HealthScore) : AnalyzerDomainResult;

    internal sealed record MemoryLeakDomainResult(
        int FinalizerQueueCount,
        int DuplicateStringPatternCount,
        ulong DuplicateStringWastedBytes,
        int HighlyReferencedObjectCount,
        long SkippedReferenceAddresses) : AnalyzerDomainResult;

    internal sealed record CollectionDomainResult(
        int TotalCollections,
        int Dictionaries,
        int Lists,
        int HashSets,
        ulong TotalWastedMemory,
        int WastefulCollectionCount) : AnalyzerDomainResult;

    internal sealed record StaticRootDomainResult(
        int RootCount,
        ulong TotalRetainedBytes,
        IReadOnlyList<NameBytesEntry>? TopRootsByRetainedBytes = null) : AnalyzerDomainResult;

    internal sealed record ReferenceChainDomainResult(
        int AnalyzedSamples,
        int RetainedSamples,
        double RetainedPercent,
        IReadOnlyList<NameCountEntry>? TopRetainedTypes = null) : AnalyzerDomainResult;

    internal sealed record ThreadDomainResult(
        int AliveThreadCount,
        int BlockedThreadCount,
        int LockHoldingThreadCount,
        int ThreadsWithActiveExceptionsCount,
        IReadOnlyDictionary<string, int> WaitPatternBreakdown) : AnalyzerDomainResult;

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
        double DiversityPercent,
        IReadOnlyList<string> TopClusterSignatures) : AnalyzerDomainResult;

    internal sealed record EventLeakDomainResult(
        int TotalEventLeakInstances,
        int TotalSubscribers,
        int StaticEventLeakCount,
        int InstanceEventLeakCount,
        IReadOnlyList<NameCountEntry>? TopPublisherEventsBySubscribers = null) : AnalyzerDomainResult;

    internal sealed record LockGraphDomainResult(
        int TotalHeldLocks,
        int ContestedLockCount,
        int MaxWaitersOnSingleLock,
        int DeadlockCandidateCount,
        IReadOnlyList<NameCountEntry>? TopContestedLockTypes = null) : AnalyzerDomainResult;
}
