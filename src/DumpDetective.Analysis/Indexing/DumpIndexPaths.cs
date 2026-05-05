namespace DumpDetective.Analysis.Indexing;

/// <summary>
/// Computes canonical paths for every index file under the per-dump <c>.dumpindex/</c>
/// subdirectory. All writers and readers must obtain paths through this class to guarantee
/// consistency.
/// </summary>
/// <remarks>
/// Directory layout:
/// <code>
/// {DumpDir}/{DumpFileName}.dumpindex/
///     ObjectIndex.bin
///     HandleSnapshot.bin
///     RootIndex.bin
///     TaskIndex.bin
///     EventCandidateIndex.bin
///     LohFreeBlockIndex.bin
///     LargeObjectIndex.bin
///     PartialRefEdgeIndex.bin
/// </code>
/// Deleting the <c>.dumpindex/</c> folder performs a full cache invalidation.
/// </remarks>
internal static class DumpIndexPaths
{
    // ── File names ────────────────────────────────────────────────────────────

    public const string ObjectIndexFile          = "ObjectIndex.bin";
    public const string HandleSnapshotFile       = "HandleSnapshot.bin";
    public const string RootIndexFile            = "RootIndex.bin";
    public const string TaskIndexFile            = "TaskIndex.bin";
    public const string EventCandidateIndexFile  = "EventCandidateIndex.bin";
    public const string LohFreeBlockIndexFile    = "LohFreeBlockIndex.bin";
    public const string LargeObjectIndexFile     = "LargeObjectIndex.bin";
    public const string PartialRefEdgeIndexFile  = "PartialRefEdgeIndex.bin";
    /// <summary>
    /// Compact binary snapshot of <see cref="TypeAggregateIndexEntry"/> records, module
    /// registry, global size buckets, and type shape cache produced during Phase 1.
    /// When this file is present alongside <see cref="ObjectIndexFile"/> the entire
    /// Phase 1 heap scan is skipped on subsequent analyses of the same dump.
    /// </summary>
    public const string TypeAggregateIndexFile   = "TypeAggregateIndex.bin";
    public const string StringDedupIndexFile     = "StringDedupIndex.bin";

    // ── Directory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path to the <c>.dumpindex/</c> directory for <paramref name="dumpPath"/>.
    /// The directory is NOT created by this method.
    /// </summary>
    public static string GetIndexDirectory(string dumpPath)
    {
        string dumpDir  = Path.GetDirectoryName(dumpPath) ?? Directory.GetCurrentDirectory();
        string dumpName = Path.GetFileName(dumpPath);
        return Path.Combine(dumpDir, dumpName + ".dumpindex");
    }

    // ── Per-file paths ────────────────────────────────────────────────────────

    public static string ObjectIndex(string dumpPath)         => Combine(dumpPath, ObjectIndexFile);
    public static string HandleSnapshot(string dumpPath)      => Combine(dumpPath, HandleSnapshotFile);
    public static string RootIndex(string dumpPath)           => Combine(dumpPath, RootIndexFile);
    public static string TaskIndex(string dumpPath)           => Combine(dumpPath, TaskIndexFile);
    public static string EventCandidateIndex(string dumpPath) => Combine(dumpPath, EventCandidateIndexFile);
    public static string LohFreeBlockIndex(string dumpPath)   => Combine(dumpPath, LohFreeBlockIndexFile);
    public static string LargeObjectIndex(string dumpPath)    => Combine(dumpPath, LargeObjectIndexFile);
    public static string PartialRefEdgeIndex(string dumpPath) => Combine(dumpPath, PartialRefEdgeIndexFile);
    public static string TypeAggregateIndex(string dumpPath)  => Combine(dumpPath, TypeAggregateIndexFile);
    public static string StringDedupIndex(string dumpPath)   => Combine(dumpPath, StringDedupIndexFile);

    public const string StringDedupIndexMetadataFile = "StringDedupIndex.meta.json";
    public static string StringDedupIndexMetadata(string dumpPath) => Combine(dumpPath, StringDedupIndexMetadataFile);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Combine(string dumpPath, string fileName) =>
        Path.Combine(GetIndexDirectory(dumpPath), fileName);

    /// <summary>
    /// Ensures the <c>.dumpindex/</c> directory exists, creating it if necessary.
    /// Returns the directory path.
    /// </summary>
    public static string EnsureDirectory(string dumpPath)
    {
        string dir = GetIndexDirectory(dumpPath);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
