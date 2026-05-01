using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Static Roots

internal sealed record StaticRootDomainResult(
    int RootCount,
    ulong TotalRetainedBytes,
    IReadOnlyList<NameBytesEntry>? TopRootsByRetainedBytes = null) : AnalyzerDomainResult;
