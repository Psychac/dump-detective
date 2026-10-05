using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DumpDetective.Cli.Console;
using DumpDetective.Reporting.Models;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Cli.Output;

/// <summary>
/// Writes every observation behind a report to a side-car next to it — observations are ~1 KB each and
/// run to ~100k per dump, so inlining them would dwarf the report itself (docs/ReportStructure/m1-claims-plan.md, D2).
/// </summary>
internal static class ObservationsSideCarWriter
{
    private const string CompressedSuffix = ".observations.json.gz";
    private const string PlainSuffix = ".observations.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<ObservationsReference> WriteAsync(
        string primaryOutputPath,
        IReadOnlyList<Observation> observations,
        bool compressed,
        CancellationToken cancellationToken)
    {
        string path = ReportSiblingPath.For(primaryOutputPath, compressed ? CompressedSuffix : PlainSuffix);

        await using (FileStream file = File.Create(path))
        {
            Stream target = compressed ? new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true) : file;
            await using (target)
            await using (var writer = new Utf8JsonWriter(target))
            {
                writer.WriteStartArray();
                for (int i = 0; i < observations.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonSerializer.Serialize(writer, observations[i], SerializerOptions);
                }

                writer.WriteEndArray();
            }
        }

        string sha256;
        await using (FileStream written = File.OpenRead(path))
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellationToken));

        ConsoleUx.ReportWritten(path);
        return new ObservationsReference(Path.GetFileName(path), sha256, observations.Count, compressed);
    }
}
