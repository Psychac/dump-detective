using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// GCRootAnalyzer domain models

public sealed record RootKindSummary(
    string Kind,
    int Count,
    ulong EstimatedRetainedBytes,
    double PctOfManagedHeap,
    bool IsExactRetainedBytes = false,
    double Gen0Fraction = 0.0,
    double Gen1Fraction = 0.0,
    double Gen2Fraction = 0.0,
    double LohFraction = 0.0);

public sealed record RootFinding(
    string RootKind,
    ulong RootAddress,
    string? FieldDescription,
    string TargetTypeName,
    ulong TargetAddress,
    ulong EstimatedRetainedBytes,
    int SeverityScore,
    bool RetainedBytesIsExact = false);

/// <summary>
/// The subgraph of objects a rooted object retains — a forward BFS from
/// <see cref="TargetAddress"/> outward into what it references, not a root-to-target chain
/// (a GC root always points directly at its target; there is no multi-hop path to find there).
/// </summary>
public sealed record RootOwnedSubgraphFinding(
    ulong TargetAddress,
    string TargetTypeName,
    string RootKind,
    IReadOnlyList<string> SubgraphTypeNames,  // type names in BFS order from the target outward
    int SubgraphNodeCount,
    bool WasCapped,
    ulong EstimatedRetainedBytes = 0,
    bool RetainedSizeWasWalked = false,
    bool RetainedSizeIsExact = false);

internal sealed record GCRootDomainResult(
    int TotalRoots,
    IReadOnlyList<RootKindSummary> ByKind,
    IReadOnlyList<RootFinding> TopRootsBySeverity,
    IReadOnlyList<RootOwnedSubgraphFinding> RootOwnedSubgraphs,
    bool SubgraphWalkCapped,
    int SubgraphWalkCappedCount,
    int DroppedZeroEstimateRootCount = 0) : AnalyzerDomainResult;
