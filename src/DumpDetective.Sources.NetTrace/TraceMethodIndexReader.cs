using System.Buffers.Binary;
using System.Text;

using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sources.NetTrace;

/// <summary>One decoded <c>trace.methods</c> record — see <see cref="TraceMethodIndexWriter"/> for
/// the authoritative on-disk layout this mirrors.</summary>
internal readonly record struct TraceMethodRecord(
    long MethodId,
    long ModuleId,
    ulong StartAddress,
    int Size,
    int Token,
    MatchFidelity Fidelity,
    string DeclaringTypeCanonicalName,
    string MethodName,
    string RawSignature);

/// <summary>
/// Reads <c>trace.methods</c> records back — the counterpart <see cref="TraceMethodIndexWriter"/>
/// didn't need until a second consumer (<c>CpuHotspotAnalyzer</c>, resolving sampled instruction
/// pointers against method address ranges) needed the same variable-length parsing
/// <c>TraceMethodIndexerRealTraceTests</c> had already written inline for its own assertions.
/// Extracted here rather than duplicated a second time.
/// </summary>
internal static class TraceMethodIndexReader
{
    public static IEnumerable<TraceMethodRecord> ReadAll(Stream stream)
    {
        // A heap-allocated buffer, not stackalloc: this is an iterator method, and a Span can't be
        // held live across a yield return boundary.
        byte[] fixedFields = new byte[8 + 8 + 8 + 4 + 4 + 1];

        while (true)
        {
            int read = stream.ReadAtLeast(fixedFields, fixedFields.Length, throwOnEndOfStream: false);
            if (read < fixedFields.Length)
                yield break;

            long methodId = BinaryPrimitives.ReadInt64LittleEndian(fixedFields);
            long moduleId = BinaryPrimitives.ReadInt64LittleEndian(fixedFields[8..]);
            ulong startAddress = BinaryPrimitives.ReadUInt64LittleEndian(fixedFields[16..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(fixedFields[24..]);
            int token = BinaryPrimitives.ReadInt32LittleEndian(fixedFields[28..]);
            var fidelity = (MatchFidelity)fixedFields[32];

            string typeName = ReadLengthPrefixedUtf8(stream);
            string methodName = ReadLengthPrefixedUtf8(stream);
            string signature = ReadLengthPrefixedUtf8(stream);

            yield return new TraceMethodRecord(methodId, moduleId, startAddress, size, token, fidelity, typeName, methodName, signature);
        }
    }

    private static string ReadLengthPrefixedUtf8(Stream stream)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        stream.ReadExactly(lenBuf);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
