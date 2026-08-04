using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Boxing & Value Type Pressure

internal sealed record BoxedTypeEntry(
    string ValueTypeName,
    int BoxCount,
    ulong TotalBoxBytes,
    bool IsEnum,
    bool HasReferenceFields);

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
    bool TypeScanCapped,
    int TypeScanCapUsed) : AnalyzerDomainResult;
