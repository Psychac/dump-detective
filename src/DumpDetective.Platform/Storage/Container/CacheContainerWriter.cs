using System.Buffers;
using System.IO.Hashing;

using DumpDetective.Sdk.Analysis;

namespace DumpDetective.Platform.Storage.Container;

/// <summary>
/// Builds <c>cache.bin</c>: one section at a time, each section's body byte-identical to
/// today's per-file formats, with the header + TOC written once all sections are complete.
/// Writes to a <c>.tmp</c> file and atomically renames it into place on <see cref="Finish"/>.
/// </summary>
internal sealed class CacheContainerWriter : IDisposable
{
    private static readonly int ReservedSectionCount = Enum.GetValues<CacheSectionId>().Length;
    private const int ChecksumBufferSize = 64 * 1024;

    // Below this, EndSection's checksum re-read is fast enough (well under a second on typical
    // disks) that per-chunk progress reporting would just be noise for the vast majority of
    // sections — Handles/Roots/Tasks/etc. Only report for sections large enough that a silent
    // checksum pass could plausibly look like a stall (e.g. the reverse-index sections, which can
    // run tens to hundreds of MB even on modest dumps).
    private const long ChecksumProgressThresholdBytes = 32L * 1024 * 1024;
    private const long ChecksumProgressReportEveryBytes = 64L * 1024 * 1024;

    private readonly string _finalPath;
    private readonly string _tmpPath;
    private readonly string? _dumpPath;
    private readonly IProgress<AnalyzerProgressReport>? _progress;
    private readonly FileStream _stream;
    private readonly List<CacheTocEntry> _entries = new(ReservedSectionCount);
    private readonly HashSet<CacheSectionId> _intendedSections = new(ReservedSectionCount);

    private CacheSectionId _activeSectionId;
    private long _activeSectionStart;
    private bool _sectionOpen;
    private bool _finished;

    /// <param name="dumpPath">
    /// Source dump path used to compute the content-addressed cache key on <see cref="Finish"/>.
    /// Optional (defaults to <c>null</c>) so existing direct test construction keeps working;
    /// omitting it just means the header's <c>DumpContentHash</c> stays zero-filled ("unknown").
    /// </param>
    /// <param name="progress">
    /// Optional — used only to report progress during <see cref="EndSection"/>'s checksum re-read
    /// for sections large enough that it could otherwise look like a stall (see
    /// <see cref="ChecksumProgressThresholdBytes"/>). Every other write path here already reports
    /// through the caller's own progress instance directly.
    /// </param>
    public CacheContainerWriter(string finalPath, string? dumpPath = null, IProgress<AnalyzerProgressReport>? progress = null)
    {
        _finalPath = finalPath;
        _dumpPath = dumpPath;
        _progress = progress;
        _tmpPath = finalPath + ".tmp";
        _stream = new FileStream(_tmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);

        // Reserve the max possible TOC size up front so section data can start right after it;
        // SectionCount in the final header may end up smaller if a section is skipped on failure.
        long dataStartOffset = CacheFileHeader.Size + (long)ReservedSectionCount * CacheTocEntry.Size;
        byte[] placeholder = new byte[dataStartOffset];
        _stream.Write(placeholder, 0, placeholder.Length);
    }

    /// <summary>
    /// Underlying stream. Callers write a section's bytes directly to this between
    /// <see cref="BeginSection"/> and <see cref="EndSection"/>, using their existing
    /// per-format write logic (including placeholder-header-then-patch idioms).
    /// </summary>
    public Stream Stream => _stream;

    /// <summary>Marks the current stream position as the start of section <paramref name="id"/>.</summary>
    public void BeginSection(CacheSectionId id)
    {
        if (_sectionOpen)
            throw new InvalidOperationException($"Section {_activeSectionId} is still open.");

        _activeSectionId = id;
        _activeSectionStart = _stream.Position;
        _sectionOpen = true;

        // Recorded on open, not on close, which is the whole point: a section that opens and then
        // aborts is exactly the case the TOC can't distinguish from one that was never attempted.
        _intendedSections.Add(id);
    }

    /// <summary>
    /// Abandons the current section without recording a TOC entry — used when a satellite writer
    /// throws mid-section. Rewinds the stream to the section's start so the partial bytes are
    /// overwritten by whatever section is opened next, matching the pre-container behavior where a
    /// failed satellite file simply never got created.
    /// </summary>
    public void AbortSection()
    {
        if (!_sectionOpen)
            throw new InvalidOperationException("No section is open.");

        _stream.Position = _activeSectionStart;
        _stream.SetLength(_activeSectionStart);
        _sectionOpen = false;
    }

    /// <summary>
    /// Closes the current section and records its TOC entry, including a checksum computed by
    /// re-reading the section's final bytes in bounded chunks. A live incremental hash during the
    /// write isn't used because satellite writers patch a placeholder record-count header in
    /// place after streaming all records, which an in-flight hash would miss.
    /// </summary>
    public void EndSection(long recordCount)
    {
        if (!_sectionOpen)
            throw new InvalidOperationException("No section is open.");

        long end = _stream.Position;
        long length = end - _activeSectionStart;
        uint checksum = ComputeChecksum(_activeSectionStart, length);
        _stream.Position = end;

        _entries.Add(new CacheTocEntry(_activeSectionId, _activeSectionStart, length, recordCount, checksum));
        _sectionOpen = false;
    }

    /// <summary>
    /// Closes the current section using a checksum the caller already computed while writing the
    /// section's bytes, skipping <see cref="EndSection(long)"/>'s re-read pass entirely. Only safe
    /// for sections written as a single contiguous streamed pass with no placeholder-header-patched-
    /// afterward step (unlike the satellite writers' record-count header, which is patched in place
    /// after streaming and would make an in-flight hash wrong) — e.g. the columnar object sections
    /// and the reverse-index bucket/directory merges, both multi-GB on large dumps where a full
    /// re-read is real added wall-clock, not the tiny satellite sections where it's negligible.
    /// </summary>
    public void EndSection(long recordCount, uint precomputedChecksum)
    {
        if (!_sectionOpen)
            throw new InvalidOperationException("No section is open.");

        long end = _stream.Position;
        long length = end - _activeSectionStart;

        _entries.Add(new CacheTocEntry(_activeSectionId, _activeSectionStart, length, recordCount, precomputedChecksum));
        _sectionOpen = false;
    }

    /// <summary>
    /// Writes one optional section, owning the whole begin/write/end/abort-and-warn shape that was
    /// previously copy-pasted at thirteen call sites — see
    /// docs/cache/cache-implementation-clean-slate-redesign.md § 6.2. <paramref name="write"/>
    /// returns the section's record count.
    /// </summary>
    /// <remarks>
    /// Only the *wrapper* is shared. Section order stays explicit at the call sites, because the
    /// build genuinely cannot write them in an arbitrary order: the columnar sections need the
    /// scratch files, the dominator sections need the reachability walk's result, and
    /// <see cref="CacheSectionId.TypeAggregates"/> must go last because its presence is what marks
    /// the build complete (§ 6.5(b)).
    /// </remarks>
    /// <returns><c>true</c> if the section was written and closed; <c>false</c> if it was aborted.</returns>
    public bool TryWriteSection(
        CacheSectionId id,
        string progressMessage,
        Func<Stream, long> write,
        List<string> warnings,
        IProgress<AnalyzerProgressReport>? progress = null,
        System.Diagnostics.Stopwatch? stopwatch = null)
    {
        try
        {
            progress?.Report(new AnalyzerProgressReport(0, progressMessage, Detail: null,
                Elapsed: stopwatch?.Elapsed ?? TimeSpan.Zero));
            BeginSection(id);
            long recordCount = write(Stream);
            EndSection(recordCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Abort is itself best-effort: if the failure happened before BeginSection, or after
            // EndSection already closed the section, there is nothing open to roll back.
            try { AbortSection(); } catch { /* no section was open */ }
            warnings.Add($"{id}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes the ids of every section this build opened, itself included, as the last section
    /// before the TOC. See <see cref="CacheSectionId.SectionManifest"/> for why open rather than
    /// close is the right moment to have recorded them.
    /// </summary>
    private void WriteSectionManifest()
    {
        BeginSection(CacheSectionId.SectionManifest);

        CacheSectionId[] ids = [.. _intendedSections.Order()];
        var hasher = new XxHash32();
        Span<byte> buf = stackalloc byte[sizeof(int)];
        foreach (CacheSectionId id in ids)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, (int)id);
            _stream.Write(buf);
            hasher.Append(buf);
        }

        EndSection(ids.Length, hasher.GetCurrentHashAsUInt32());
    }

    private uint ComputeChecksum(long start, long length)
    {
        bool reportProgress = _progress is not null && length >= ChecksumProgressThresholdBytes;
        var stopwatch = reportProgress ? System.Diagnostics.Stopwatch.StartNew() : null;
        long processedSinceLastReport = 0;

        var hasher = new XxHash32();
        _stream.Position = start;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChecksumBufferSize);
        try
        {
            long remaining = length;
            long processed = 0;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = _stream.Read(buffer, 0, toRead);
                if (read <= 0)
                    break;

                hasher.Append(buffer.AsSpan(0, read));
                remaining -= read;
                processed += read;

                if (reportProgress)
                {
                    processedSinceLastReport += read;
                    if (processedSinceLastReport >= ChecksumProgressReportEveryBytes)
                    {
                        processedSinceLastReport = 0;
                        _progress!.Report(new AnalyzerProgressReport(0, $"verifying {_activeSectionId} section",
                            Detail: $"{processed / (1024 * 1024)}/{length / (1024 * 1024)} MB", Elapsed: stopwatch!.Elapsed));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return hasher.GetCurrentHashAsUInt32();
    }

    /// <summary>Writes the real header + TOC and atomically renames the <c>.tmp</c> file into place.</summary>
    public void Finish()
    {
        if (_finished)
            throw new InvalidOperationException("Finish() already called.");
        if (_sectionOpen)
            throw new InvalidOperationException($"Section {_activeSectionId} was never closed.");

        WriteSectionManifest();
        _stream.Flush();

        long tocOffset = CacheFileHeader.Size;
        _stream.Position = tocOffset;
        foreach (CacheTocEntry entry in _entries)
            entry.WriteTo(_stream);

        byte[]? dumpContentHash = TryComputeDumpContentHash();

        _stream.Position = 0;
        new CacheFileHeader(_entries.Count, tocOffset, dumpContentHash).WriteTo(_stream);

        _stream.Flush(flushToDisk: true);
        _stream.Dispose();

        File.Move(_tmpPath, _finalPath, overwrite: true);
        _finished = true;
    }

    /// <summary>
    /// Computes the dump's content signature for the header. Returns <c>null</c> (zero-filled,
    /// "unknown") if no dump path was supplied or hashing fails — a missing/replaced dump is
    /// still caught on the next full build, so a hashing hiccup here shouldn't be fatal.
    /// </summary>
    private byte[]? TryComputeDumpContentHash()
    {
        if (_dumpPath is null)
            return null;

        try
        {
            return DumpContentHasher.Compute(_dumpPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>If <see cref="Finish"/> was never called, discards the in-progress <c>.tmp</c> file.</summary>
    public void Dispose()
    {
        if (_finished)
            return;

        _stream.Dispose();
        try { File.Delete(_tmpPath); } catch { /* best-effort cleanup */ }
    }
}
