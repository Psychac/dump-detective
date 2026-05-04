namespace DumpDetective.Core.Options;

public sealed class MemoryLeakOptions
{
    public int TopFinalizerTypesToShow { get; init; } = 10;
    public int TopHighlyReferencedObjectsToShow { get; init; } = 15;

    public int HighReferenceThreshold { get; init; } = 50;
    public int MaxDuplicateStringLength { get; init; } = 500;
    public int MinDuplicateStringCount { get; init; } = 10;
    public int MaxReferenceAddresses { get; init; } = 1_000_000;

    /// <summary>
    /// Maximum number of objects subjected to full reference-field enumeration during
    /// the incoming-reference-count pass. Each traced object requires at least one
    /// <c>heap.GetObject()</c> call against the dump file, which is the primary
    /// bottleneck on large (multi-GB) dumps.
    /// Default: 2 000 000. Set to 0 to disable the limit (only safe on small dumps).
    /// When the limit is reached <see cref="MemoryLeakDomainResult.ObjectScanCapped"/>
    /// is set to <c>true</c> and a confidence note is emitted in the report.
    /// </summary>
    public int MaxLeakScanObjects { get; init; } = 2_000_000;
}