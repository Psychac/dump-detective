using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;

namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Computes a fast, sampled content signature for a dump file — the cache's identity key
/// instead of path+mtime, so a dump copied/moved to a new path still hits the cache, and a
/// same-path dump silently replaced with different content doesn't. Hashes the file's length
/// plus a few fixed-size windows (start, middle, end) rather than the whole file, since fully
/// hashing a 25GB+ dump would cost as much as the heap scan the cache exists to avoid.
/// </summary>
internal static class DumpContentHasher
{
    public const int HashSize = 32;
    private const int WindowSize = 1024 * 1024;

    /// <summary>Computes the 32-byte signature: file length (8 bytes) + XxHash64 over sampled windows (8 bytes), zero-padded.</summary>
    public static byte[] Compute(string dumpPath)
    {
        byte[] result = new byte[HashSize];
        using var stream = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = stream.Length;
        BinaryPrimitives.WriteInt64LittleEndian(result, length);

        var hasher = new XxHash64();
        AppendWindow(hasher, stream, 0, length);
        if (length > WindowSize)
        {
            AppendWindow(hasher, stream, length / 2, length);
            AppendWindow(hasher, stream, length - WindowSize, length);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), hasher.GetCurrentHashAsUInt64());
        return result;
    }

    private static void AppendWindow(XxHash64 hasher, FileStream stream, long offset, long fileLength)
    {
        int size = (int)Math.Min(WindowSize, fileLength - offset);
        if (size <= 0)
            return;

        stream.Position = offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(0, size), size, throwOnEndOfStream: false);
            hasher.Append(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="dumpPath"/>'s current content matches <paramref name="storedHash"/>.
    /// An all-zero stored hash means the cache predates content hashing, or hashing failed when the cache was
    /// built (e.g. a permission error) — treated as "unknown", not a mismatch, so it doesn't force a spurious
    /// rebuild the one time this happens.
    /// </summary>
    public static bool Matches(string dumpPath, ReadOnlySpan<byte> storedHash)
    {
        if (storedHash.Length != HashSize)
            return false;

        if (storedHash.IndexOfAnyExcept((byte)0) < 0)
            return true;

        try
        {
            return Compute(dumpPath).AsSpan().SequenceEqual(storedHash);
        }
        catch
        {
            return false;
        }
    }
}
