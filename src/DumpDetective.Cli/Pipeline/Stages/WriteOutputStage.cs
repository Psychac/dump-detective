using DumpDetective.Cli.Console;
using DumpDetective.Cli.Services;

namespace DumpDetective.Cli.Pipeline.Stages;

internal sealed class WriteOutputStage : IAnalysisStage
{
    public string Name => "Write output";

    public async Task ExecuteAsync(SingleDumpPipelineState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!string.IsNullOrWhiteSpace(state.Resolved.OutputPath))
            {
                string outPath = state.Resolved.OutputPath!;
                await File.WriteAllTextAsync(outPath, state.RenderedReport, cancellationToken);
                ConsoleUx.ReportWritten(outPath);

                // Persist any analyzer-produced artifacts alongside the report.
                var doc = state.ReportDocument;
                if (doc?.Artifacts is { Count: > 0 })
                {
                    string outDir = Path.GetDirectoryName(outPath) ?? Directory.GetCurrentDirectory();
                    // Make artifacts folder specific to the dump being analyzed to avoid collisions
                    // Prefer the resolved dump path for naming; fall back to the report document DumpPath
                    string resolvedDump = state.Resolved?.DumpPath ?? doc?.DumpPath ?? string.Empty;
                    string dumpBaseName = !string.IsNullOrWhiteSpace(resolvedDump)
                        ? Path.GetFileNameWithoutExtension(resolvedDump)
                        : "default";
                    // sanitize folder name
                    foreach (var c in Path.GetInvalidFileNameChars()) dumpBaseName = dumpBaseName.Replace(c, '_');
                    string artifactsDir = Path.Combine(outDir, "artifacts", dumpBaseName);
                    Directory.CreateDirectory(artifactsDir);

                    foreach (var a in doc.Artifacts!)
                    {
                        try
                        {
                            string target = Path.Combine(artifactsDir, a.FileName);
                            // If the artifact is an on-disk temp file produced by an analyzer, move it
                            if (!string.IsNullOrEmpty(a.FilePath) && File.Exists(a.FilePath))
                            {
                                try
                                {
                                    // Prefer atomic move
                                    File.Move(a.FilePath, target);
                                    ConsoleUx.ReportWritten(target);
                                }
                                catch
                                {
                                    try
                                    {
                                        File.Copy(a.FilePath, target, overwrite: true);
                                        ConsoleUx.ReportWritten(target);
                                        File.Delete(a.FilePath);
                                    }
                                    catch
                                    {
                                        // Fall back to writing content if provided
                                        if (a.Content is not null)
                                        {
                                            await File.WriteAllTextAsync(target, a.Content, cancellationToken);
                                            ConsoleUx.ReportWritten(target);
                                        }
                                    }
                                }
                            }
                            else if (a.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Content))
                            {
                                // Legacy: base64-encoded gzip payload stored in Content
                                try
                                {
                                    byte[] bytes = Convert.FromBase64String(a.Content);
                                    await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                                    ConsoleUx.ReportWritten(target);
                                }
                                catch
                                {
                                    await File.WriteAllTextAsync(target, a.Content, cancellationToken);
                                    ConsoleUx.ReportWritten(target);
                                }
                            }
                            else
                            {
                                await File.WriteAllTextAsync(target, a.Content ?? string.Empty, cancellationToken);
                                ConsoleUx.ReportWritten(target);
                            }
                        }
                        catch
                        {
                            // Non-fatal: continue writing remaining artifacts.
                        }
                    }
                }
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
