using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Boxing & Value Type Pressure

internal sealed record BoxedTypeEntry(
    string ValueTypeName,
    int BoxCount,
    ulong TotalBoxBytes,
    bool IsEnum,
    bool HasReferenceFields,
    long Gen0Count,
    long Gen2Count,
    double Gen2Fraction,
    // Missing IEquatable<T> means equality comparisons (Dictionary/HashSet keys, List.Contains)
    // box the value on every call via the non-generic object.Equals fallback.
    bool HasIEquatable);

internal sealed record OversizedTypeEntry(
    string TypeName,
    int StaticSize,
    int Count);

internal sealed record StructPaddingEntry(
    string TypeName,
    int TotalFieldBytes,
    int StructSize,
    int WastedPaddingBytes,
    double WasteRatio);

internal sealed record BoxingDomainResult(
    long TotalBoxedObjects,
    ulong TotalBoxedBytes,
    IReadOnlyList<BoxedTypeEntry> TopBoxedTypes,
    long BoxedEnumCount,
    ulong BoxedEnumBytes,
    long NullableBoxedCount,
    ulong NullableBoxedBytes,
    long OversizedValueTypeInstanceCount,
    IReadOnlyList<OversizedTypeEntry> TopOversizedTypes,
    IReadOnlyList<StructPaddingEntry> TopPaddingWasteTypes,
    ulong AggregatePaddingWasteBytes,
    double AvgBoxedInstanceBytes,
    // Summed over all boxed types (same population as TopBoxedTypes), giving a consistent
    // denominator for Gen2 fraction trending — mirrors AsyncStateMachineDomainResult.TotalGen2Count.
    long TotalGen2BoxedCount = 0) : AnalyzerDomainResult;
