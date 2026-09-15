using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// ObjectShapeAnalyzer

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
    ulong TotalSize,
    bool IsFinalizable,
    bool IsValueType,
    bool IsArray,
    int BaseTypeChainDepth,
    int InterfaceCount,
    ObjectShapeCategory Category,
    /// <summary>
    /// Instances of this type currently in Gen2 (exact count from <c>TypeAggregateIndexEntry.Gen2Count</c>).
    /// Gen2 objects are rescanned by every full/Gen2 GC, unlike Gen0/Gen1 instances that are collected
    /// far more often but scanned far less overall — <c>ReferenceFields * Gen2InstanceCount</c> is the
    /// retention-adjusted GC scan cost, distinct from the total-instance-weighted cost above.
    /// </summary>
    ulong Gen2InstanceCount = 0);

internal sealed record ObjectShapeAnalyzerDomainResult(
    IReadOnlyList<TypeShapeProfile> TopReferenceHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopValueHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopBalancedTypes,
    int TotalTypesAnalyzed,
    double AvgRefFieldsPerType,
    long TotalGcScanWork,
    /// <summary>Reference-, value-, and balanced-shape types ranked by <c>ReferenceFields * Gen2InstanceCount</c>
    /// descending — the retention-adjusted GC scan cost, surfacing types whose GC scan pressure is durable
    /// (paid every Gen2 collection) rather than transient (collected cheaply in Gen0).</summary>
    IReadOnlyList<TypeShapeProfile> TopGen2RetainedTypes,
    /// <summary>Σ(ReferenceFields * Gen2InstanceCount) across all analyzed types — the retention-adjusted
    /// counterpart to <see cref="TotalGcScanWork"/>.</summary>
    long TotalGen2GcScanWork) : AnalyzerDomainResult;
