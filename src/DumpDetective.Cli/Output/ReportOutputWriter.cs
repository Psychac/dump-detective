using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Diagnostics;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpDetective.Cli.Output;

internal sealed class ReportOutputWriter
{
    internal static readonly Regex EmbeddedReportJsonPattern = new(
        "<script\\b([^>]*\\bid\\s*=\\s*(['\"])report-json\\2[^>]*)>([\\s\\S]*?)</script>",
        RegexOptions.IgnoreCase);

    private static readonly Regex GzipBase64EncodingAttribute = new(
        "\\bdata-encoding\\s*=\\s*(['\"])gzip-base64\\1",
        RegexOptions.IgnoreCase);

    internal static string DecodeEmbeddedReportJson(Match embeddedReportJson)
    {
        string scriptAttributes = embeddedReportJson.Groups[1].Value;
        string body = embeddedReportJson.Groups[3].Value;
        if (!GzipBase64EncodingAttribute.IsMatch(scriptAttributes))
            return body;

        using var compressed = new MemoryStream(Convert.FromBase64String(body.Trim()));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public async Task WriteAsync(
        ResolvedExecutionOptions resolved,
        AnalysisReportDocument? document,
        string renderedReport,
        IReadOnlyList<ReportArtifact>? artifacts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (string.IsNullOrWhiteSpace(resolved.OutputPath))
                return;

            string outPath = resolved.OutputPath;
            if (resolved.Report.SeparateJson && resolved.Report.Format == Core.Configuration.ReportFormat.Html)
            {
                string html = renderedReport ?? string.Empty;
                var match = EmbeddedReportJsonPattern.Match(html);
                string json = match.Success ? DecodeEmbeddedReportJson(match) : string.Empty;
                string outDir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
                string jsonPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(outPath) + ".json");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
                    ConsoleUx.ReportWritten(jsonPath);

                    string replacement = "<script id=\"report-json\" type=\"application/json\">{" + "\"_external\": \"" + Path.GetFileName(jsonPath) + "\"}</script>";
                    if (match.Success)
                        html = html.Substring(0, match.Index) + replacement + html.Substring(match.Index + match.Length);

                    await File.WriteAllTextAsync(outPath, html, cancellationToken);
                    ConsoleUx.ReportWritten(outPath);
                }
                else
                {
                    await File.WriteAllTextAsync(outPath, renderedReport ?? string.Empty, cancellationToken);
                    ConsoleUx.ReportWritten(outPath);
                }
            }
            else
            {
                await File.WriteAllTextAsync(outPath, renderedReport ?? string.Empty, cancellationToken);
                ConsoleUx.ReportWritten(outPath);
            }

            if (artifacts is not { Count: > 0 })
                return;

            string outDirectory = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
            string dumpBaseName = !string.IsNullOrWhiteSpace(resolved.DumpPath)
                ? Path.GetFileNameWithoutExtension(resolved.DumpPath)
                : "default";

            foreach (char c in Path.GetInvalidFileNameChars())
                dumpBaseName = dumpBaseName.Replace(c, '_');

            string artifactsDir = Path.Combine(outDirectory, "artifacts", dumpBaseName);
            Directory.CreateDirectory(artifactsDir);

            var artifactsIndex = new List<object>(capacity: artifacts.Count);
            foreach (var artifact in artifacts)
            {
                try
                {
                    string target = Path.Combine(artifactsDir, artifact.FileName);
                    if (!string.IsNullOrEmpty(artifact.FilePath) && File.Exists(artifact.FilePath))
                    {
                        try
                        {
                            File.Move(artifact.FilePath, target);
                            ConsoleUx.ReportWritten(target);
                        }
                        catch
                        {
                            try
                            {
                                File.Copy(artifact.FilePath, target, overwrite: true);
                                ConsoleUx.ReportWritten(target);
                                File.Delete(artifact.FilePath);
                            }
                            catch
                            {
                                if (artifact.Content is not null)
                                {
                                    await File.WriteAllTextAsync(target, artifact.Content, cancellationToken);
                                    ConsoleUx.ReportWritten(target);
                                }
                            }
                        }
                    }
                    else if (artifact.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(artifact.Content))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(artifact.Content);
                            await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                            ConsoleUx.ReportWritten(target);
                        }
                        catch
                        {
                            await File.WriteAllTextAsync(target, artifact.Content, cancellationToken);
                            ConsoleUx.ReportWritten(target);
                        }
                    }
                    else
                    {
                        await File.WriteAllTextAsync(target, artifact.Content ?? string.Empty, cancellationToken);
                        ConsoleUx.ReportWritten(target);
                    }

                    try
                    {
                        var fi = new FileInfo(target);
                        artifactsIndex.Add(new
                        {
                            Analyzer = artifact.Analyzer,
                            FileName = artifact.FileName,
                            ContentType = artifact.ContentType,
                            SizeBytes = fi.Exists ? fi.Length : 0L,
                            OriginalPath = string.IsNullOrEmpty(artifact.FilePath) ? null : artifact.FilePath,
                            ProducedAtUtc = DateTime.UtcNow
                        });
                    }
                    catch
                    {
                    }
                }
                catch
                {
                    // Non-fatal: continue writing remaining artifacts.
                }
            }

            try
            {
                string idxPath = Path.Combine(artifactsDir, "index.json");
                var opts = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(idxPath, JsonSerializer.Serialize(artifactsIndex, opts), cancellationToken);
                ConsoleUx.ReportWritten(idxPath);

                if (artifacts.Any(a => a.FileName.EndsWith(".ndjson.gz", StringComparison.OrdinalIgnoreCase)))
                {
                    ConsoleUx.Info("One or more analyzers produced NDJSON+gzip exports. To stream and pretty-print: 'gzip -cd <file>.ndjson.gz | jq -C '.' (or extract with 7-Zip and open in VS Code). A human-friendly JSON is also provided when available.");
                }
            }
            catch
            {
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OutputWriteException("Failed while writing analysis output.", ex);
        }
    }
}
