using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;

using System.Text.Json;

namespace DumpDetective.Cli.Services;

internal sealed class ReportOutputWriter
{
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
                string pattern = "<script[^>]*\\bid\\s*=\\s*(['\"])(report-json)\\1[^>]*>([\\s\\S]*?)</script>";
                var match = System.Text.RegularExpressions.Regex.Match(html, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string json = match.Success ? match.Groups[3].Value : string.Empty;
                string outDir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
                string jsonPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(outPath) + ".json");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    await File.WriteAllTextAsync(jsonPath, json, cancellationToken);
                    ConsoleUx.ReportWritten(jsonPath);

                    string replacement = "<script id=\"report-json\" type=\"application/json\">{\"_external\": \"" + Path.GetFileName(jsonPath) + "\"}</script>";
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