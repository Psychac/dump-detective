using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

internal sealed class RetainedTypeInfo
{
    public string TypeName { get; set; } = string.Empty;
    public int Count { get; set; }
    public ulong TotalSize { get; set; }
}

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
    Evidence? Evidence = null,
    IReadOnlyList<RetainedTypeInfo>? TopRetainedTypes = null,
    bool ScanWasCapped = false,
    bool ContainsCollections = false,
    bool ContainsEventHandlers = false,
    string? AssemblyLoadContextInfo = null);
