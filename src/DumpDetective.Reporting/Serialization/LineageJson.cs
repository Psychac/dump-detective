using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Reporting.Serialization;

/// <summary>
/// Writes observation lineage into <c>report.json</c> once instead of at every citation: each citation list becomes its position in the
/// document's <see cref="LineageTable"/> (docs/ReportStructure/m4-payload-plan.md, report.json fix 2). The same ids were otherwise
/// repeated about five times each, as 32-character strings.
/// </summary>
internal static class LineageJson
{
    /// <summary>Makes every <c>IReadOnlyList&lt;ObservationId&gt;</c> in the document write as its position in <paramref name="index"/>.</summary>
    public static Action<JsonTypeInfo> ReferenceLists(LineageIndex index)
    {
        var converter = new ListReferenceConverter(index);
        return typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
                return;

            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (property.PropertyType == typeof(IReadOnlyList<ObservationId>))
                    property.CustomConverter = converter;
            }
        };
    }

    private sealed class ListReferenceConverter(LineageIndex index) : JsonConverter<IReadOnlyList<ObservationId>>
    {
        public override IReadOnlyList<ObservationId> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A citation is a position in the document's lineage table; report.json lineage is write-only.");

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<ObservationId> value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(index.ListOf(value));
    }
}

/// <summary>
/// Writes the lineage table with the ids 32 to a line and each citation list on one line, so the file stays readable and a line never
/// holds the whole table.
/// </summary>
internal sealed class LineageTableJsonConverter : JsonConverter<LineageTable>
{
    private const int IdsPerLine = 32;
    private const int BytesPerId = 35;
    private const int BytesPerIndex = 11;

    public override LineageTable Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("report.json lineage is write-only.");

    public override void Write(Utf8JsonWriter writer, LineageTable value, JsonSerializerOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>(IdsPerLine * BytesPerId + 64);

        writer.WriteStartObject();
        writer.WritePropertyName("ids");
        writer.WriteStartArray();
        for (int start = 0; start < value.Ids.Count; start += IdsPerLine)
        {
            int end = Math.Min(start + IdsPerLine, value.Ids.Count);
            StartLine(buffer, writer, options);
            Span<byte> span = buffer.GetSpan((end - start) * BytesPerId);
            int position = 0;
            for (int i = start; i < end; i++)
            {
                if (i > start)
                    span[position++] = (byte)',';

                span[position++] = (byte)'"';
                Utf8Formatter.TryFormat(value.Ids[i].Value, span[position..], out int written, 'N');
                position += written;
                span[position++] = (byte)'"';
            }

            buffer.Advance(position);
            writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
        }

        writer.WriteEndArray();

        writer.WritePropertyName("lists");
        writer.WriteStartArray();
        foreach (int[] list in value.Lists)
        {
            StartLine(buffer, writer, options);
            Span<byte> span = buffer.GetSpan(list.Length * BytesPerIndex + 2);
            int position = 0;
            span[position++] = (byte)'[';
            for (int i = 0; i < list.Length; i++)
            {
                if (i > 0)
                    span[position++] = (byte)',';

                Utf8Formatter.TryFormat(list[i], span[position..], out int written);
                position += written;
            }

            span[position++] = (byte)']';
            buffer.Advance(position);
            writer.WriteRawValue(buffer.WrittenSpan, skipInputValidation: true);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // A raw value gets no line break or indent from the writer, so each line asks for its own.
    private static void StartLine(ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        buffer.Clear();
        if (options.WriteIndented)
            InlineJson.WriteLineStart(buffer, writer.CurrentDepth);
    }
}
