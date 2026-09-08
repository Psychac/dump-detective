using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;

namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Opens <c>cache.bin</c> and hands out bounded, section-scoped views. The TOC is small
/// (32 bytes per section, ~25 sections) and is read once into memory by <see cref="TryOpen"/>;
/// <see cref="TryOpenSection"/> and <see cref="TryOpenSectionAccessor"/> then hand back a view
/// bounded to that section's byte range.
/// </summary>
/// <remarks>
/// <para>
/// One instance is a <b>session</b>: it remembers which sections it has already checksum-verified
/// and does not re-verify them. That is the whole point — see
/// docs/cache/cache-redesign-measurements.md § 5/§ 6. Verifying a section costs a full pass over its
/// bytes (68.8 ms for the four object columns on a 14.6M-object dump, 197% of the scan it gates),
/// and the previous design repeated it on every open: ~20 times per run, of which 8 came from
/// <c>HeapIndexScanDispatcher</c>'s per-worker range enumerations all re-verifying the same whole
/// section to read disjoint slices of it.
/// </para>
/// <para>
/// The mapping handle deliberately stays per-call rather than per-session. An earlier revision held
/// one <see cref="MemoryMappedFile"/> open for the session, which locks <c>cache.bin</c> on Windows
/// and breaks any caller that later deletes or replaces the index directory. The cost it saved was
/// never measured and is a <c>CreateFileMapping</c> syscall — microseconds against the 68.8 ms this
/// class actually exists to avoid — so the lock was real and the saving was not.
/// </para>
/// <para>
/// <b>Contract change this makes deliberately:</b> integrity is checked once per session rather than
/// on every open, so corruption appearing <i>mid-run</i> is no longer caught. That is the right trade
/// for a file that <see cref="CacheContainerWriter.Finish"/> renames into place and never rewrites,
/// but it is a real narrowing and is recorded as such.
/// </para>
/// <para>
/// Instances must stay scoped to one dump — never cached in a static keyed by path, since two dumps
/// are analysed in one process for baseline/trend comparison. <see cref="Cache.HeapIndexCache"/>
/// owns the long-lived one.
/// </para>
/// </remarks>
internal sealed class CacheContainerReader
{
    private readonly string _containerPath;
    private readonly IReadOnlyDictionary<CacheSectionId, CacheTocEntry> _sections;
    private readonly byte[] _dumpContentHash;

    // Per-section gate, so N workers first-touching the *same* section verify it once between them
    // while different sections still verify concurrently. A single lock would serialise every
    // worker behind one 68.8 ms hash — the exact stall this class exists to remove.
    private readonly ConcurrentDictionary<CacheSectionId, object> _verifyGates = new();
    private readonly ConcurrentDictionary<CacheSectionId, bool> _verified = new();

    // DD_PERF_CACHE_SESSION=1 — process-wide tally answering open question 2 in
    // docs/cache/cache-redesign-measurements.md: how many section opens a real run performs, and
    // how many of them the session's memoization spared from a full re-hash. Diagnostics only; the
    // counters are Interlocked and off the hot path (one increment per *open*, not per record).
    internal static readonly bool PerfLogSession =
        Environment.GetEnvironmentVariable("DD_PERF_CACHE_SESSION") == "1";
    internal static long ContainersOpened;
    internal static long SectionOpens;
    internal static long VerificationsPerformed;
    internal static long VerificationsSkipped;
    internal static long BytesVerified;
    // Wall-clock spent inside verify() calls. Separate from BytesVerified because the two diverge
    // wildly under memory pressure: hashing runs at 4.6–6.9 GB/s (measurements § 5) but hashes
    // through a mapped view, so the real cost is demand-paging the section in — 8.3 MB/s observed on
    // the 27.5 GB run's first dominator section (docs/cache/cache-redesign-runtime-rebalance.md §E.1).
    // Without this counter the read-side share of that cost is invisible.
    internal static long VerificationTicks;
    // Per-section verification tally: which sections are hashed more than once per run, i.e. which
    // are reached through more than one CacheContainerReader instance.
    internal static readonly ConcurrentDictionary<CacheSectionId, int> VerifiedPerSection = new();

    internal static string PerfSummary() =>
        $"[PERF] CacheSession: {Interlocked.Read(ref ContainersOpened):N0} container opens, " +
        $"{Interlocked.Read(ref SectionOpens):N0} section opens, " +
        $"{Interlocked.Read(ref VerificationsPerformed):N0} verified / " +
        $"{Interlocked.Read(ref VerificationsSkipped):N0} skipped by memoization, " +
        $"{Interlocked.Read(ref BytesVerified) / (1024.0 * 1024):N1} MiB hashed in " +
        $"{TimeSpan.FromTicks(Interlocked.Read(ref VerificationTicks)).TotalSeconds:N1} s " +
        $"({(Interlocked.Read(ref VerificationTicks) == 0 ? 0 : Interlocked.Read(ref BytesVerified) / (1024.0 * 1024) / TimeSpan.FromTicks(Interlocked.Read(ref VerificationTicks)).TotalSeconds):N1} MiB/s)"
        + Environment.NewLine + "[PERF] CacheSession: sections verified more than once: "
        + (VerifiedPerSection.Any(kv => kv.Value > 1)
            ? string.Join(", ", VerifiedPerSection.Where(kv => kv.Value > 1)
                .OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))
            : "(none)")
        + Environment.NewLine + "[PERF] CacheSession: sections TOUCHED: "
        + string.Join(", ", VerifiedPerSection.Keys.OrderBy(k => (int)k))
        + Environment.NewLine + "[PERF] CacheSession: sections NEVER touched: "
        + string.Join(", ", Enum.GetValues<CacheSectionId>()
            .Where(id => !VerifiedPerSection.ContainsKey(id)).OrderBy(id => (int)id));

    private CacheContainerReader(string containerPath, IReadOnlyDictionary<CacheSectionId, CacheTocEntry> sections, byte[] dumpContentHash)
    {
        _containerPath = containerPath;
        _sections = sections;
        _dumpContentHash = dumpContentHash;
    }

    /// <summary>
    /// Runs <paramref name="verify"/> at most once per section id for this session's lifetime.
    /// A section that fails verification is remembered as failed, so a corrupt section stays
    /// treated as missing without being re-hashed on every subsequent attempt.
    /// </summary>
    private bool VerifyOnce(CacheSectionId id, Func<bool> verify)
    {
        if (PerfLogSession) Interlocked.Increment(ref SectionOpens);

        if (_verified.TryGetValue(id, out bool cached))
        {
            if (PerfLogSession) Interlocked.Increment(ref VerificationsSkipped);
            return cached;
        }

        lock (_verifyGates.GetOrAdd(id, static _ => new object()))
        {
            if (_verified.TryGetValue(id, out cached))
            {
                if (PerfLogSession) Interlocked.Increment(ref VerificationsSkipped);
                return cached;
            }

            if (PerfLogSession)
            {
                Interlocked.Increment(ref VerificationsPerformed);
                VerifiedPerSection.AddOrUpdate(id, 1, static (_, n) => n + 1);
                if (_sections.TryGetValue(id, out CacheTocEntry e))
                    Interlocked.Add(ref BytesVerified, e.Length);
            }

            long startTicks = PerfLogSession ? Stopwatch.GetTimestamp() : 0;
            bool ok = verify();
            if (PerfLogSession)
                Interlocked.Add(ref VerificationTicks, Stopwatch.GetElapsedTime(startTicks).Ticks);

            _verified[id] = ok;
            return ok;
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="dumpPath"/>'s current content matches the hash
    /// stamped in this container's header — the cheapest possible gate before falling through
    /// to a section-level read, since it's a handful of sampled-window hashes rather than a
    /// per-section parse. A header with an all-zero hash (predates content hashing, or hashing
    /// failed at build time) is treated as "unknown" and passes.
    /// </summary>
    public bool MatchesDumpContent(string dumpPath) => DumpContentHasher.Matches(dumpPath, _dumpContentHash);

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

            reader = new CacheContainerReader(containerPath, sections, header.DumpContentHash);
            if (PerfLogSession) Interlocked.Increment(ref ContainersOpened);
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

    /// <summary>
    /// Section ids the build that wrote this container opened but never closed — i.e. writes that
    /// failed and were downgraded to a warning. Empty for a healthy container, and also empty for
    /// one written without a <see cref="CacheSectionId.SectionManifest"/>, which cannot be
    /// distinguished from healthy and so is not treated as a fault.
    /// </summary>
    public IReadOnlyList<CacheSectionId> LostSections()
    {
        if (!_sections.TryGetValue(CacheSectionId.SectionManifest, out CacheTocEntry manifest)
            || manifest.Length <= 0
            || manifest.Length % sizeof(int) != 0)
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(_containerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            stream.Position = manifest.Offset;

            byte[] buffer = new byte[manifest.Length];
            if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
                return [];

            var lost = new List<CacheSectionId>();
            for (int i = 0; i < buffer.Length; i += sizeof(int))
            {
                var id = (CacheSectionId)BitConverter.ToInt32(buffer, i);
                if (!_sections.ContainsKey(id))
                    lost.Add(id);
            }

            return lost;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool TryGetSectionInfo(CacheSectionId id, out CacheTocEntry entry) => _sections.TryGetValue(id, out entry);

    private const int ChecksumBufferSize = 64 * 1024;

    /// <summary>
    /// Memory-maps <c>cache.bin</c> and hands back a read-only view stream bounded to section
    /// <paramref name="id"/>'s byte range. Returns <c>false</c> if the section isn't present in
    /// the TOC (e.g. it failed to write during the original build and was skipped) or if the
    /// section's bytes no longer match the checksum recorded at build time — corruption is
    /// treated exactly like a missing section so every caller's existing cold-cache fallback
    /// handles it without special-casing.
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
        MemoryMappedViewStream view = mmf.CreateViewStream(entry.Offset, entry.Length, MemoryMappedFileAccess.Read);

        if (!VerifyOnce(id, () =>
            {
                bool ok = VerifyChecksum(view, entry.Checksum);
                view.Position = 0;
                return ok;
            }))
        {
            view.Dispose();
            return false;
        }

        view.Position = 0;
        sectionStream = view;
        return true;
    }

    private static bool VerifyChecksum(Stream sectionStream, uint expected)
    {
        var hasher = new XxHash32();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChecksumBufferSize);
        try
        {
            int read;
            while ((read = sectionStream.Read(buffer, 0, buffer.Length)) > 0)
                hasher.Append(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32() == expected;
    }

    // Largest chunk handed to a single XxHash32.Append/ReadOnlySpan<byte> call — Span length is
    // int-bound, so multi-GB sections (heap object columns on large dumps) must be hashed in slices.
    private const int MaxZeroCopyChunkBytes = 1 << 30;

    /// <summary>
    /// Same contract as <see cref="TryOpenSection"/> (checksum verified before the caller sees any
    /// data, corruption treated as a missing section) but hands back a
    /// <see cref="MemoryMappedViewAccessor"/> instead of a <see cref="Stream"/> so callers on
    /// heap-scaled hot paths (see <see cref="DumpDetective.Analysis.Indexing.ObjectIndexReader"/>)
    /// can read directly off the mapped pages via <see cref="MemoryMappedViewAccessor.SafeMemoryMappedViewHandle"/>
    /// instead of copying through a managed buffer. Callers own the returned accessor and must
    /// dispose it.
    /// </summary>
    public bool TryOpenSectionAccessor(CacheSectionId id, out MemoryMappedViewAccessor? accessor, out long length)
    {
        accessor = null;
        length = 0;
        if (!_sections.TryGetValue(id, out CacheTocEntry entry))
            return false;

        if (entry.Length == 0)
            return true;

        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(_containerPath, FileMode.Open,
            mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor view = mmf.CreateViewAccessor(entry.Offset, entry.Length, MemoryMappedFileAccess.Read);

        if (!VerifyOnce(id, () => VerifyChecksumZeroCopy(view, entry.Length, entry.Checksum)))
        {
            view.Dispose();
            return false;
        }

        accessor = view;
        length = entry.Length;
        return true;
    }

    private static unsafe bool VerifyChecksumZeroCopy(MemoryMappedViewAccessor view, long length, uint expected)
    {
        var hasher = new XxHash32();
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            byte* dataPtr = basePtr + view.PointerOffset;
            long remaining = length;
            long offset = 0;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(MaxZeroCopyChunkBytes, remaining);
                hasher.Append(new ReadOnlySpan<byte>(dataPtr + offset, chunk));
                offset += chunk;
                remaining -= chunk;
            }
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        return hasher.GetCurrentHashAsUInt32() == expected;
    }
}
