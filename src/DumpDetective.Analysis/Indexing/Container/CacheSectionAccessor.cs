namespace DumpDetective.Analysis.Indexing.Container;

/// <summary>
/// Read-only stream view over one section of <c>cache.bin</c>. Exposes the section's own
/// <c>[0, Length)</c> coordinate space so existing per-format reader code — written against
/// standalone files — works unmodified against a shared container: <c>Position</c>, <c>Length</c>,
/// <c>Seek</c>, and <c>Read</c> all behave as if this were a freshly opened file containing only
/// this section's bytes.
/// </summary>
internal sealed class CacheSectionStream : Stream
{
    private readonly Stream _inner;
    private readonly long _sectionOffset;
    private readonly long _sectionLength;

    public CacheSectionStream(Stream inner, long sectionOffset, long sectionLength)
    {
        _inner = inner;
        _sectionOffset = sectionOffset;
        _sectionLength = sectionLength;
        _inner.Position = sectionOffset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _sectionLength;

    public override long Position
    {
        get => _inner.Position - _sectionOffset;
        set => _inner.Position = _sectionOffset + value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _sectionLength - Position;
        if (remaining <= 0)
            return 0;

        int bounded = (int)Math.Min(count, remaining);
        return _inner.Read(buffer, offset, bounded);
    }

    public override int Read(Span<byte> buffer)
    {
        long remaining = _sectionLength - Position;
        if (remaining <= 0)
            return 0;

        int bounded = (int)Math.Min(buffer.Length, remaining);
        return _inner.Read(buffer[..bounded]);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => _sectionLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = target;
        return target;
    }

    public override void Flush() => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
