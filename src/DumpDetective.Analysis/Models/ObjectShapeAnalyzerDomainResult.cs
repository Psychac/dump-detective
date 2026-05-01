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
    bool IsFinalizable,
    bool IsValueType,
    int BaseTypeChainDepth,
    int InterfaceCount,
    ObjectShapeCategory Category);

internal sealed record ObjectShapeAnalyzerDomainResult(
    IReadOnlyList<TypeShapeProfile> TopReferenceHeavyTypes,
    IReadOnlyList<TypeShapeProfile> TopValueHeavyTypes,
    int TotalTypesAnalyzed,
    double AvgRefFieldsPerType) : AnalyzerDomainResult;
