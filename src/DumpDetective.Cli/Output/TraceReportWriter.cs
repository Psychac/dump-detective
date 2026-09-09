using System.Text.Json;
using System.Text.Json.Serialization;

using DumpDetective.Cli.Console;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Reporting.Models;

namespace DumpDetective.Cli.Output;

/// <summary>
/// Writes <see cref="TraceSessionReport"/> to disk as <c>report.json</c> — unconditionally, per
/// § 8 step 6, unlike the dump side's <c>ReportOutputWriter</c>, which today writes nothing at all
/// when <c>--output</c> is omitted. Trace analysis had no report output whatsoever before this (see
/// <c>TraceOrchestrationService</c>'s prior remarks); there is no existing "silent by default"
/// behavior here worth preserving.
/// </summary>
internal static class TraceReportWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Resolves the report path — <paramref name="requestedOutputPath"/> if the caller gave one,
    /// else <c>&lt;tracePath&gt;.report.json</c> next to the trace file itself, mirroring the dump
    /// side's own default-output-path convention (<c>ConfigurationResolver.BuildOutputPath</c>).
    /// </summary>
    public static string ResolveOutputPath(string tracePath, string? requestedOutputPath) =>
        string.IsNullOrWhiteSpace(requestedOutputPath)
            ? Path.ChangeExtension(tracePath, ".report.json")
            : requestedOutputPath;

    public static async Task WriteAsync(TraceSessionReport report, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(report, SerializerOptions);
            await File.WriteAllTextAsync(outputPath, json, cancellationToken);
            ConsoleUx.ReportWritten(outputPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutputWriteException($"Failed while writing trace report.json to '{outputPath}': {ex.Message}", ex);
        }
    }
}
