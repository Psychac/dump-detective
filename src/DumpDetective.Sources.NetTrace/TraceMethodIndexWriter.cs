using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Writes the <c>trace.methods</c> section body (see
/// <c>CacheSectionId.TraceMethods</c> in DumpDetective.Platform). Callers accumulate records via
/// <see cref="Add"/> then call <see cref="Flush"/> once.
/// </summary>
/// <remarks>
/// <para>
/// Record layout, one per distinct method (variable length, little-endian fixed fields followed by
/// three length-prefixed UTF-8 strings):
///   MethodId (8) | ModuleId (8) | StartAddress (8) | Size (4) | Token (4) | Fidelity (1) |
///   DeclaringTypeCanonicalName (4-byte length + UTF-8 bytes) |
///   MethodName (4-byte length + UTF-8 bytes) |
///   RawSignature (4-byte length + UTF-8 bytes)
/// </para>
/// <para>
/// Deliberately no inner magic/version header, unlike dump satellite writers such as
/// <c>TaskIndexWriter</c> — those predate the container consolidation and kept a
/// self-describing header for that reason. This section only ever exists inside a
/// <c>CacheContainerWriter</c> section, which already carries record count, checksum, and format
/// version via the container's own TOC and header; a second copy of that bookkeeping here would be
/// pure redundancy, not a hedge against anything.
/// </para>
/// <para>
/// <b>RawSignature is not yet parsed into individual canonicalized parameter types.</b> Trace-side
/// method signatures come from CLR ETW event payloads in IL-assembly notation
/// (<c>"void  (value class System.Web.EtwTraceConfigType,int)"</c>) — a different convention from
/// ClrMD's reflection-style names, confirmed against real captured data
/// (<c>tools/MethodEventProbe</c>). Splitting this into a properly canonicalized parameter list
/// needs its own IL-signature grammar handling, which is real, separate work — see
/// docs/refactor/modularity/phase-6-trace-source.md. Storing the raw signature text now is honest
/// about that gap rather than a silent placeholder.
/// </para>
/// </remarks>
internal sealed class TraceMethodIndexWriter : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;

    private readonly Stream _stream;
    private byte[] _buf;
    private long _recordCount;
    private bool _disposed;

    public TraceMethodIndexWriter(Stream stream)
    {
        _stream = stream;
        _buf = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    public void Add(
        long methodId,
        long moduleId,
        ulong startAddress,
        int size,
        int token,
        MatchFidelity fidelity,
        string declaringTypeCanonicalName,
        string methodName,
        string rawSignature)
    {
        int maxRecordBytes = 8 + 8 + 8 + 4 + 4 + 1
            + 4 + Encoding.UTF8.GetByteCount(declaringTypeCanonicalName)
            + 4 + Encoding.UTF8.GetByteCount(methodName)
            + 4 + Encoding.UTF8.GetByteCount(rawSignature);

        if (maxRecordBytes > _buf.Length)
        {
            byte[] bigger = ArrayPool<byte>.Shared.Rent(maxRecordBytes);
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = bigger;
        }

        Span<byte> span = _buf;
        int offset = 0;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], methodId); offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], moduleId); offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], startAddress); offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], size); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], token); offset += 4;
        span[offset] = (byte)fidelity; offset += 1;
        offset += WriteLengthPrefixedUtf8(span[offset..], declaringTypeCanonicalName);
        offset += WriteLengthPrefixedUtf8(span[offset..], methodName);
        offset += WriteLengthPrefixedUtf8(span[offset..], rawSignature);

        _stream.Write(_buf, 0, offset);
        _recordCount++;
    }

    private static int WriteLengthPrefixedUtf8(Span<byte> destination, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(destination, byteCount);
        Encoding.UTF8.GetBytes(value, destination.Slice(4, byteCount));
        return 4 + byteCount;
    }

    /// <summary>Flushes the underlying stream and returns the number of records written — the
    /// caller passes this to <c>CacheContainerWriter.EndSection</c>.</summary>
    public long Flush()
    {
        _stream.Flush();
        return _recordCount;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buf);
    }
}
