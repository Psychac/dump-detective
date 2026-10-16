using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Keeps the repeated, bulky parts of an indented <c>report.json</c> on one line each. An indented writer puts every array element, and
/// every key of an object inside it, on its own line: a table row of ten cells takes about 400 bytes instead of about 100, an unknown
/// cell <c>{"u":"na"}</c> takes three lines, and a list of observation ids takes a line per id. The values are unchanged, so any JSON
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

    private static void WriteLineStart(ArrayBufferWriter<byte> buffer, int depth)
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

    /// <summary>
    /// Makes every <c>IReadOnlyList&lt;ObservationId&gt;</c> in the document (a finding's <c>derivedFrom</c>, and the observations of each
    /// support and counter statement) write on one line when the output is indented.
    /// </summary>
    public static void InlineObservationIdLists(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.PropertyType == typeof(IReadOnlyList<ObservationId>))
                property.CustomConverter = ObservationIdListConverter.Instance;
        }
    }

    private sealed class ObservationIdListConverter : JsonConverter<IReadOnlyList<ObservationId>>
    {
        public static readonly ObservationIdListConverter Instance = new();

        // Same shape the default list converter reads: an array of "N"-format id strings.
        public override IReadOnlyList<ObservationId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected a JSON array of observation ids.");

            var ids = new List<ObservationId>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                ids.Add(new ObservationId(Guid.ParseExact(reader.GetString() ?? throw new JsonException("Expected an observation id string."), "N")));

            return ids;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<ObservationId> value, JsonSerializerOptions options)
        {
            if (!options.WriteIndented)
            {
                writer.WriteStartArray();
                for (int i = 0; i < value.Count; i++)
                    writer.WriteStringValue(value[i].Value.ToString("N"));
                writer.WriteEndArray();
                return;
            }

            // An id is 32 hex digits, two quotes and a comma; the whole list is built once, at its exact size, and written as one value.
            const int BytesPerId = 35;
            var buffer = new ArrayBufferWriter<byte>(value.Count * BytesPerId + 2);
            Span<byte> span = buffer.GetSpan(value.Count * BytesPerId + 2);
            int position = 0;
            span[position++] = (byte)'[';
            for (int i = 0; i < value.Count; i++)
            {
                if (i > 0)
                    span[position++] = (byte)',';

                span[position++] = (byte)'"';
                Utf8Formatter.TryFormat(value[i].Value, span[position..], out int written, 'N');
                position += written;
                span[position++] = (byte)'"';
            }

            span[position++] = (byte)']';
            buffer.Advance(position);
            writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
        }
    }
}
