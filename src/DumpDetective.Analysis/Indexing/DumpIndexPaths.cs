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
///     cache.bin
/// </code>
/// <c>cache.bin</c> is a single container file holding every index as a TOC-addressed
/// section (see <see cref="DumpDetective.Analysis.Indexing.Container.CacheContainerFormat"/>).
/// Deleting the <c>.dumpindex/</c> folder performs a full cache invalidation.
/// </remarks>
internal static class DumpIndexPaths
{
    // ── File names ────────────────────────────────────────────────────────────

    public const string CacheContainerFile = "cache.bin";

    // ── Directory ─────────────────────────────────────────────────────────────

    // Populated by ResolveCacheDirectory once per dump path so the ~40 call sites below
    // that only take a dumpPath (readers/writers/analyzers) transparently pick up a
    // --cache-dir or temp-folder fallback without every one of them taking a new parameter.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_resolvedDirectories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the path to the <c>.dumpindex/</c> directory for <paramref name="dumpPath"/>.
    /// If <see cref="ResolveCacheDirectory"/> was previously called for this dump path, returns
    /// that resolved directory (user override or temp fallback); otherwise returns the
    /// dump-colocated default. The directory is NOT created by this method.
    /// </summary>
    public static string GetIndexDirectory(string dumpPath)
    {
        if (s_resolvedDirectories.TryGetValue(Path.GetFullPath(dumpPath), out string? resolved))
        {
            return resolved;
        }

        return GetColocatedDirectory(dumpPath);
    }

    private static string GetColocatedDirectory(string dumpPath)
    {
        string dumpDir = Path.GetDirectoryName(dumpPath) ?? Directory.GetCurrentDirectory();
        string dumpName = Path.GetFileName(dumpPath);
        return Path.Combine(dumpDir, dumpName + ".dumpindex");
    }

    /// <summary>
    /// Resolves the cache directory to use for <paramref name="dumpPath"/> and remembers it so
    /// every subsequent <see cref="GetIndexDirectory"/> call (and everything derived from it)
    /// for this dump path returns the same location. Priority order:
    /// 1. <paramref name="userCacheDir"/> (<c>--cache-dir</c>) — hard failure if not writable.
    /// 2. Dump-colocated <c>{dumpPath}.dumpindex/</c> folder.
    /// 3. <c>%TEMP%/dumpdetective-cache/&lt;hash&gt;/</c> best-effort fallback (temp folders can be
    ///    evicted at any time, so this is not durable across runs, but keeps the tool usable when
    ///    the dump lives on read-only/network storage).
    /// Throws <see cref="IOException"/> if none of the above are writable.
    /// </summary>
    public static string ResolveCacheDirectory(string dumpPath, string? userCacheDir, Action<string>? onTempFallback = null)
    {
        string resolved = ResolveCore(dumpPath, userCacheDir, onTempFallback);
        s_resolvedDirectories[Path.GetFullPath(dumpPath)] = resolved;
        return resolved;
    }

    private static string ResolveCore(string dumpPath, string? userCacheDir, Action<string>? onTempFallback)
    {
        if (!string.IsNullOrWhiteSpace(userCacheDir))
        {
            string userDir = Path.Combine(userCacheDir, Path.GetFileName(dumpPath) + ".dumpindex");
            if (TryEnsureWritable(userDir))
            {
                return userDir;
            }

            throw new IOException($"--cache-dir '{userCacheDir}' is not writable. Specify a different --cache-dir.");
        }

        string colocated = GetColocatedDirectory(dumpPath);
        if (TryEnsureWritable(colocated))
        {
            return colocated;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "dumpdetective-cache", HashDumpPath(dumpPath));
        if (TryEnsureWritable(tempDir))
        {
            onTempFallback?.Invoke(tempDir);
            return tempDir;
        }

        throw new IOException(
            $"Cannot write a cache index for '{dumpPath}': the dump folder and the temp folder are both " +
            "unwritable. Specify a writable location with --cache-dir.");
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string HashDumpPath(string dumpPath)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dumpPath).ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Path to the single container file holding every index section.</summary>
    public static string CacheContainer(string dumpPath) => Combine(dumpPath, CacheContainerFile);

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
