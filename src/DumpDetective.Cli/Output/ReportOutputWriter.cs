using DumpDetective.Cli.Console;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Sdk.Observations;

using System.Diagnostics;
using System.Text.Json;

namespace DumpDetective.Cli.Output;

internal sealed class ReportOutputWriter(IEnumerable<IReportSink> sinks)
{
    private readonly IReadOnlyList<IReportSink> _sinks = sinks.ToArray();

    private const int MaxReportedEntityProblems = 10;

    // The report is still written: a reader is better served by a report with one dead link than by none.
    private static void ReportBrokenEntityReferences(AnalysisReportDocument document)
    {
        IReadOnlyList<string> problems = EntityReferenceValidator.Validate(document);
        if (problems.Count == 0)
            return;

        ConsoleUx.Warning($"{problems.Count} broken entity reference(s) in the report; those links will not open correctly.");
        for (int i = 0; i < problems.Count && i < MaxReportedEntityProblems; i++)
            ConsoleUx.Warning("  " + problems[i]);
        if (problems.Count > MaxReportedEntityProblems)
            ConsoleUx.Warning($"  ...and {problems.Count - MaxReportedEntityProblems} more.");
    }

    public async Task WriteAsync(
        ResolvedExecutionOptions resolved,
        AnalysisReportDocument? document,
        IReadOnlyList<Observation> observations,
        IReadOnlyList<ReportArtifact>? artifacts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (string.IsNullOrWhiteSpace(resolved.OutputPath))
                return;

            string outPath = resolved.OutputPath;
            if (document is not null)
            {
                var step = Stopwatch.StartNew();
                if (observations.Count > 0)
                {
                    ObservationsReference sideCar = await ObservationsSideCarWriter.WriteAsync(
                        outPath, observations, compressed: !resolved.Report.ObservationsUncompressed, cancellationToken);
                    ConsoleUx.OutputStep($"observations side-car ({observations.Count:N0} observations)", step.Elapsed);

                    step.Restart();
                    IReadOnlyList<EntityRecord>? entities = document is SingleDumpReportDocument
                        ? EntityTableBuilder.Build(observations, document.Findings)
                        : null;
                    ConsoleUx.OutputStep($"entity table ({entities?.Count ?? 0:N0} entities)", step.Elapsed);

                    step.Restart();
                    EntitySummary? entitySummary = entities is null ? null : EntitySummaryBuilder.Build(observations, entities, document.Findings);
                    ConsoleUx.OutputStep("entity summary", step.Elapsed);

                    step.Restart();
                    EntityRelations? relations = entities is null ? null : EntityRelationBuilder.FromFacts(observations, entities);
                    ConsoleUx.OutputStep("entity relations", step.Elapsed);

                    document = document with
                    {
                        Observations = sideCar,
                        Entities = entities,
                        EntitySummary = entitySummary,
                        Relations = relations,
                    };
                }

                step.Restart();
                ReportBrokenEntityReferences(document);
                ConsoleUx.OutputStep("entity reference check", step.Elapsed);

                foreach (IReportSink sink in _sinks)
                {
                    step.Restart();
                    await sink.PublishAsync(resolved, document, cancellationToken);
                    ConsoleUx.OutputStep($"{sink.Name} output", step.Elapsed);
                }
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
