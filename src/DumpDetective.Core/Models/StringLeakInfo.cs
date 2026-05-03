namespace DumpDetective.Core.Models;

// OPT-#12: Converted from class to struct to improve dictionary locality and eliminate per-entry
// heap allocation + object header overhead. Use CollectionsMarshal.GetValueRefOrAddDefault for
// in-place mutation to avoid the struct-copy-on-update issue.
internal struct StringLeakInfo
{
    public string? Preview;
    public int Count;
    public ulong TotalSize;
    // Representative sample addresses captured when fingerprinting
    public ulong[]? SampleAddresses;
    // Dominant method-table observed for this fingerprint (approximate)
    public ulong DominantMethodTable;
}
