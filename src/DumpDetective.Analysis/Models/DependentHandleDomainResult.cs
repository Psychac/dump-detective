using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Dependent Handles

internal sealed record DependentHandleDomainResult(
    int DependentHandleCount,
    int ResolvedEdgeCount,
    int UnresolvedTargetCount,
    double UnresolvedPercent,
    IReadOnlyList<NameCountEntry>? TopSourceTypes = null,
    IReadOnlyList<NameCountEntry>? TopTargetTypes = null,
    IReadOnlyList<NameCountEntry>? TopSourceTargetEdges = null) : AnalyzerDomainResult;
