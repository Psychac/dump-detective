using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

internal sealed record DominatorDomainResult(
    int CandidateCount,
    int AnalyzedCount,
    ulong TotalEstimatedRetainedBytes,
    IReadOnlyList<TypeSnapshot> TopDominatorTypes,
    bool HeuristicOnly = true,
    int MaxBreadth = 0,
    int MaxDepth = 20) : AnalyzerDomainResult;