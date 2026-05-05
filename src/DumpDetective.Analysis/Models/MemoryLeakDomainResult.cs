using DumpDetective.Core.Models;

namespace DumpDetective.Analysis.Models;

// Memory Leak

internal sealed record MemoryLeakDomainResult(
    int FinalizerQueueCount,
    int HighlyReferencedObjectCount,
    long SkippedReferenceAddresses,
    IReadOnlyList<NameCountEntry>? TopFinalizerTypes = null,
    IReadOnlyList<HighlyReferencedObjectSnapshot>? TopHighlyReferencedObjects = null,
    /// <summary>
    /// True when the incoming-reference-count pass was stopped early because the number
    /// of objects traced reached <see cref="DumpDetective.Core.Options.MemoryLeakOptions.MaxLeakScanObjects"/>.
    /// Highly-referenced-object results are partial when this is set.
    /// </summary>
    bool ObjectScanCapped = false,
    /// <summary>
    /// True when the disk-backed index was in use and the incoming-reference-count scan
    /// was skipped entirely to avoid O(N × fields) random reads against the dump file.
    /// Highly-referenced-object detection is unavailable; use memory index mode on this
    /// dump if you need it (requires a machine with enough RAM).
    /// </summary>
    bool ReferenceCountingSkipped = false) : AnalyzerDomainResult;

// DuplicateStringSnapshot moved to DumpDetective.Core.Models.DuplicateStringSnapshot
internal sealed record HighlyReferencedObjectSnapshot(ulong Address, string TypeName, ulong Size, int IncomingReferences);
