using System.IO.MemoryMappedFiles;

namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Opens <c>cache.bin</c> and hands out bounded, section-scoped streams. The TOC is small
/// (32 bytes per section, ~10 sections) and is read once into memory by <see cref="TryOpen"/>;
/// each <see cref="TryOpenSection"/> call then memory-maps the container and hands back a
/// <see cref="MemoryMappedViewStream"/> bounded to that section's byte range. The mapping
/// handle is unnamed (safe for concurrent analyzers per
/// <see cref="DumpDetective.Core.Abstractions.IAnalyzer.IsThreadSafe"/>) and closed once the
/// view is created — per <see cref="MemoryMappedFile"/> semantics the OS-level mapping stays
/// alive for the view's lifetime, so readers get page-cache-backed random access instead of a
/// fresh <see cref="FileStream"/> handle per call.
/// </summary>
internal sealed class CacheContainerReader
{
    private readonly string _containerPath;
    private readonly IReadOnlyDictionary<CacheSectionId, CacheTocEntry> _sections;

    private CacheContainerReader(string containerPath, IReadOnlyDictionary<CacheSectionId, CacheTocEntry> sections)
    {
        _containerPath = containerPath;
        _sections = sections;
    }

    /// <summary>
    /// Attempts to open the container at <paramref name="containerPath"/> and parse its TOC.
    /// Returns <c>false</c> on a missing file, bad magic, or unsupported format version — treated
    /// by callers the same way a missing/invalid <c>TypeAggregateIndex.bin</c> was treated before
    /// this migration: a cold cache, not an error.
    /// </summary>
    public static bool TryOpen(string containerPath, out CacheContainerReader? reader)
    {
        reader = null;
        if (!File.Exists(containerPath))
            return false;

        try
        {
            using var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);

            if (!CacheFileHeader.TryRead(stream, out CacheFileHeader header))
                return false;

            stream.Position = header.TocOffset;
            var sections = new Dictionary<CacheSectionId, CacheTocEntry>(header.SectionCount);
            byte[] buf = new byte[CacheTocEntry.Size];
            for (int i = 0; i < header.SectionCount; i++)
            {
                if (stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) < buf.Length)
                    return false;

                CacheTocEntry entry = CacheTocEntry.ReadFrom(buf);
                sections[entry.SectionId] = entry;
            }

            reader = new CacheContainerReader(containerPath, sections);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool ContainsSection(CacheSectionId id) => _sections.ContainsKey(id);

    public bool TryGetSectionInfo(CacheSectionId id, out CacheTocEntry entry) => _sections.TryGetValue(id, out entry);

    /// <summary>
    /// Memory-maps <c>cache.bin</c> and hands back a read-only view stream bounded to section
    /// <paramref name="id"/>'s byte range. Returns <c>false</c> if the section isn't present in
    /// the TOC (e.g. it failed to write during the original build and was skipped).
    /// </summary>
    public bool TryOpenSection(CacheSectionId id, out Stream? sectionStream)
    {
        sectionStream = null;
        if (!_sections.TryGetValue(id, out CacheTocEntry entry))
            return false;

        // A zero-length view means "map to end of file" per MemoryMappedFile semantics, which
        // is wrong for an empty section sitting mid-file — short-circuit instead.
        if (entry.Length == 0)
        {
            sectionStream = Stream.Null;
            return true;
        }

        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(_containerPath, FileMode.Open,
            mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        sectionStream = mmf.CreateViewStream(entry.Offset, entry.Length, MemoryMappedFileAccess.Read);
        return true;
    }
}
