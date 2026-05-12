using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Reference Chains

internal sealed record ReferenceChainDomainResult(
    int AnalyzedSamples,
    int RetainedSamples,
    double RetainedPercent,
    IReadOnlyList<NameCountEntry>? TopRetainedTypes = null,
    IReadOnlyList<string>? SampleReferenceChains = null,
    IReadOnlyList<ReferenceTypeSampleSnapshot>? TopTypeSampleTraces = null,
    int TraversalLimitedSamples = 0) : AnalyzerDomainResult;

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
