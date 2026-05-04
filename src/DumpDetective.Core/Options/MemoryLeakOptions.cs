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

    public static MemoryLeakOptions Preset(AnalysisProfile profile) => profile switch
    {
        AnalysisProfile.Fast => new MemoryLeakOptions
        {
            TopFinalizerTypesToShow = 5,
            TopHighlyReferencedObjectsToShow = 8,
            HighReferenceThreshold = 75,
            MaxDuplicateStringLength = 300,
            MinDuplicateStringCount = 20,
            MaxReferenceAddresses = 250_000,
            MaxLeakScanObjects = 500_000
        },
        AnalysisProfile.Full => new MemoryLeakOptions
        {
            TopFinalizerTypesToShow = 25,
            TopHighlyReferencedObjectsToShow = 40,
            HighReferenceThreshold = 30,
            MaxDuplicateStringLength = 2_000,
            MinDuplicateStringCount = 5,
            MaxReferenceAddresses = 2_000_000,
            MaxLeakScanObjects = 5_000_000
        },
        _ => new MemoryLeakOptions()
    };

    public static MemoryLeakOptions Default { get; } = Preset(AnalysisProfile.Balanced);
}