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

internal sealed record FinalizableObjectDomainResult(
    int TotalFinalizableObjects,
    ulong TotalFinalizableBytes,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    int FinalizerQueueCount,
    ulong FinalizerQueueRetainedBytes,
    bool PotentialResurrectionDetected,
    IReadOnlyList<TypeGenerationProfile> TopFinalizableTypesByGen2Count,
    IReadOnlyList<FinalizerQueueEntry> TopQueueEntriesByRetainedSize) : AnalyzerDomainResult;
