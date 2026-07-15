namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Shared helper for opening sections from the cache container.
/// Encapsulates the common pattern: open container, then open section within it.
/// </summary>
internal static class CacheSectionHelper
{
    /// <summary>
    /// Opens a section from the cache container in one call.
    /// Returns false if the container is missing/invalid or the section doesn't exist.
    /// Caller is responsible for disposing the returned stream.
    /// </summary>
    public static bool TryOpenCacheSection(string containerPath, CacheSectionId sectionId, out Stream? stream)
    {
        stream = null;
        if (string.IsNullOrWhiteSpace(containerPath))
            return false;

        if (!CacheContainerReader.TryOpen(containerPath, out CacheContainerReader? reader) || reader is null)
            return false;

        return reader.TryOpenSection(sectionId, out stream);
    }
}
