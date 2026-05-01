using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Boxing & Value Type Pressure

internal sealed record BoxedTypeEntry(
    string ValueTypeName,
    int BoxCount,
    ulong TotalBoxBytes,
    bool IsEnum);

internal sealed record StructPaddingEntry(
    string TypeName,
    int TotalFieldBytes,
    int StructSize,
    int WastedPaddingBytes,
    double WasteRatio);

internal sealed record BoxingDomainResult(
    int TotalBoxedObjects,
    ulong TotalBoxedBytes,
    IReadOnlyList<BoxedTypeEntry> TopBoxedTypes,
    int BoxedEnumCount,
    ulong BoxedEnumBytes,
    int OversizedValueTypeCount,
    IReadOnlyList<StructPaddingEntry> TopPaddingWasteTypes,
    bool TypeScanCapped) : AnalyzerDomainResult;
