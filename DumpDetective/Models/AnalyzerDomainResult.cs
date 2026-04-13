using DumpDetective.Utilities;

namespace DumpDetective.Models
{
    internal abstract record AnalyzerDomainResult;

    internal sealed record TypeSnapshot(string TypeName, int Count, ulong TotalBytes, ulong LohBytes);

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

    internal sealed record ModuleDomainResult(
        int TotalModules,
        int DynamicModules,
        int VersionConflictGroups,
        IReadOnlyList<string> ConflictingAssemblyNames) : AnalyzerDomainResult;

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
        int CanceledTasks) : AnalyzerDomainResult;

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
        ulong TotalRetainedBytes) : AnalyzerDomainResult;

    internal sealed record ReferenceChainDomainResult(
        int AnalyzedSamples,
        int RetainedSamples,
        double RetainedPercent) : AnalyzerDomainResult;

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
        int PinnedHandleTargets) : AnalyzerDomainResult;

    internal sealed record LohFragmentationDomainResult(
        int SegmentCount,
        ulong TotalBytes,
        ulong FreeBytes,
        double FragmentationPercent,
        ulong LargestFreeBlock) : AnalyzerDomainResult;

    internal sealed record DependentHandleDomainResult(
        int DependentHandleCount,
        int ResolvedEdgeCount,
        int UnresolvedTargetCount,
        double UnresolvedPercent) : AnalyzerDomainResult;

    internal sealed record ThreadStackClusterDomainResult(
        int AliveThreadCount,
        int UniqueClusters,
        double DiversityPercent,
        IReadOnlyList<string> TopClusterSignatures) : AnalyzerDomainResult;

    internal sealed record EventLeakDomainResult(
        int TotalEventLeakInstances,
        int TotalSubscribers,
        int StaticEventLeakCount,
        int InstanceEventLeakCount) : AnalyzerDomainResult;
}
