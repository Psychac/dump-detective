using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// GCRootAnalyzer domain models

public sealed record RootKindSummary(
    string Kind,
    int Count,
    ulong EstimatedRetainedBytes,
    double PctOfManagedHeap,
    bool IsExactRetainedBytes = false);

public sealed record RootFinding(
    string RootKind,
    ulong RootAddress,
    string? FieldDescription,
    string TargetTypeName,
    ulong TargetAddress,
    ulong EstimatedRetainedBytes,
    int SeverityScore,
    bool RetainedBytesIsExact = false);

public sealed record RootPathFinding(
    ulong TargetAddress,
    string TargetTypeName,
    string RootKind,
    IReadOnlyList<string> PathTypeNames,  // forward BFS from target outward (owned subgraph), not root-to-target chain
    int PathLength,
    bool WasCapped,
    ulong EstimatedRetainedBytes = 0,
    bool RetainedSizeWasWalked = false,
    bool RetainedSizeIsExact = false);

internal sealed record GCRootDomainResult(
    int TotalRoots,
    IReadOnlyList<RootKindSummary> ByKind,
    IReadOnlyList<RootFinding> TopRootsBySeverity,
    IReadOnlyList<RootPathFinding> RootPaths,
    bool PathSearchCapped,
    int PathSearchCappedCount) : AnalyzerDomainResult;
