namespace DumpDetective.Analysis.Indexing.Dominator;

/// <summary>
/// JSON payload of the <c>DominatorTreeMetadata</c> cache.bin section (§10.4, Batch 2b,
/// docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md) — mirrors
/// <c>ForwardIndexMetadata</c>'s mutable-property shape for <see cref="System.Text.Json"/>.
/// </summary>
internal sealed class DominatorTreeMetadata
{
    public ulong TotalRetainedBytes { get; set; }
    public List<DominatorTypeRetainedBytes> ByMethodTable { get; set; } = new();
}

/// <summary>One <c>MethodTable</c>'s exact retained-bytes total, summed over every reachable instance of it.</summary>
internal sealed class DominatorTypeRetainedBytes
{
    public ulong MethodTable { get; set; }
    public ulong RetainedBytes { get; set; }
}
