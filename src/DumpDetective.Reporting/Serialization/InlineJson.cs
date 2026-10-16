using System.Buffers;
using System.Text.Json;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Keeps the repeated, bulky parts of an indented <c>report.json</c> on one line each. An indented writer puts every array element, and
/// every key of an object inside it, on its own line: a table row of ten cells takes about 400 bytes instead of about 100, and an unknown
/// cell <c>{"u":"na"}</c> takes three lines. The values are unchanged, so any JSON
/// reader sees the same document; only whitespace differs (docs/ReportStructure/m4-payload-plan.md, report.json fix 1).
/// </summary>
internal static class InlineJson
{
    [ThreadStatic] private static ArrayBufferWriter<byte>? _buffer;
    [ThreadStatic] private static Utf8JsonWriter? _writer;
    [ThreadStatic] private static System.Text.Encodings.Web.JavaScriptEncoder? _writerEncoder;

    private const int KeepBufferUpTo = 1 << 20;

    /// <summary>
    /// A compact writer over a reused buffer. Write one complete JSON value to it, then pass it to <see cref="End"/>. A raw value gets no
    /// line break or indentation from the outer writer, so an array element asks for its own: <paramref name="elementOf"/> is the outer
    /// writer when the value is an element of an array.
    /// </summary>
    public static Utf8JsonWriter Begin(JsonSerializerOptions options, Utf8JsonWriter? elementOf = null)
    {
        ArrayBufferWriter<byte> buffer = _buffer ??= new ArrayBufferWriter<byte>(4096);
        buffer.Clear();
        if (elementOf is not null)
            WriteLineStart(buffer, elementOf.CurrentDepth);
        if (_writer is null || !ReferenceEquals(_writerEncoder, options.Encoder))
        {
            _writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = options.Encoder });
            _writerEncoder = options.Encoder;
        }
        else
        {
            _writer.Reset(buffer);
        }

        return _writer;
    }

    internal static void WriteLineStart(ArrayBufferWriter<byte> buffer, int depth)
    {
        string newLine = Environment.NewLine;
        int length = newLine.Length + depth * 2;
        Span<byte> span = buffer.GetSpan(length);
        for (int i = 0; i < newLine.Length; i++)
            span[i] = (byte)newLine[i];

        span.Slice(newLine.Length, depth * 2).Fill((byte)' ');
        buffer.Advance(length);
    }

    /// <summary>Hands the value written since <see cref="Begin"/> to <paramref name="target"/> as one raw line.</summary>
    public static void End(Utf8JsonWriter target)
    {
        _writer!.Flush();
        target.WriteRawValue(_buffer!.WrittenSpan, skipInputValidation: true);
        if (_buffer.Capacity > KeepBufferUpTo)
            _buffer = null;
    }
}
