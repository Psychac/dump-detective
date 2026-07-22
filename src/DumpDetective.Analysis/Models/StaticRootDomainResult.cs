using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Static Roots

internal sealed record StaticRootDomainResult(
    int RootCount,
    ulong TotalRetainedBytes,
    IReadOnlyList<StaticRootSnapshot>? TopRootsByRetainedBytes = null) : AnalyzerDomainResult;

internal sealed record StaticRootSnapshot(
    string RootDescription,
    ulong TotalMemoryImpact,
    int ObjectsKeptAlive,
    string TypeName = "",
    Evidence? Evidence = null);
