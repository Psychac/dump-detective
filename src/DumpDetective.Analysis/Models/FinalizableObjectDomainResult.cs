using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// FinalizableObjectAnalyzer domain models

internal sealed record FinalizerQueueEntry(
    ulong Address,
    string TypeName,
    ulong ShallowSize,
    ulong EstimatedRetainedBytes,
    bool IsDisposableType,
    bool DisposedFieldFound,
    bool DisposedFieldValue);

internal sealed record QueueTypeStatistic(
    string TypeName,
    int QueueCount);

internal sealed record FinalizableObjectDomainResult(
    int TotalFinalizableObjects,
    ulong TotalFinalizableBytes,
    long Gen0Count,
    long Gen1Count,
    long Gen2Count,
    long LohCount,
    int FinalizerQueueCount,
    ulong FinalizerQueueRetainedBytes,
    bool IsRetainedEstimatePartial,
    bool HasUndisposedDisposableInQueue,
    IReadOnlyList<TypeGenerationProfile> TopFinalizableTypesByGen2Count,
    IReadOnlyList<QueueTypeStatistic> TopQueueTypesByCount,
    IReadOnlyList<FinalizerQueueEntry> TopQueueEntriesByRetainedSize) : AnalyzerDomainResult;
