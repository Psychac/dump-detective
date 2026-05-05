using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DumpDetective.Cli.Pipeline.Stages;
using DumpDetective.Cli.Pipeline;
using DumpDetective.Cli.Services;
using DumpDetective.Reporting.Models;
using DumpDetective.Core.Models;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Integration;

public sealed class WriteOutputStageTests
{
    [Fact]
    public async Task ExecuteAsync_Moves_OnDiskArtifactIntoArtifactsFolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"dd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Prepare a dummy report output path
            string reportPath = Path.Combine(tempDir, "report.html");

            // Create a small gzipped temp file to simulate analyzer output
            string tmpGz = Path.Combine(Path.GetTempPath(), $"dd-artifact-{Guid.NewGuid():N}.ndjson.gz");
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("{\"hello\":\"world\"}\n");
            using (var fs = File.Create(tmpGz))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
            {
                gz.Write(payload, 0, payload.Length);
            }

            var artifact = new ReportArtifact(
                Analyzer: "Test",
                FileName: Path.GetFileName(tmpGz),
                Content: null,
                ContentType: "application/gzip",
                FilePath: tmpGz);

            var doc = new AnalysisReportDocument { Artifacts = new[] { artifact } };

            var state = new SingleDumpPipelineState
            {
                Resolved = ResolvedExecutionOptionsFactory.Create(reportPath),
                ActiveAnalyzers = Array.Empty<DumpDetective.Core.Abstractions.IAnalyzer>(),
                RenderedReport = "dummy",
                ReportDocument = doc
            };

            var stage = new WriteOutputStage();
            await stage.ExecuteAsync(state, CancellationToken.None);

            // Expected artifacts path is artifacts/<dump-base-name> where dump-base-name comes from Resolved.DumpPath
            string dumpBase = Path.GetFileNameWithoutExtension(state.Resolved.DumpPath);
            string artifactsDir = Path.Combine(Path.GetDirectoryName(reportPath)!, "artifacts", dumpBase);
            string target = Path.Combine(artifactsDir, Path.GetFileName(tmpGz));

            File.Exists(target).Should().BeTrue("artifact should be moved into artifacts folder");
            // Original temp file should be gone (moved)
            File.Exists(tmpGz).Should().BeFalse("temp artifact should be moved or deleted by stage");

            // Validate gz content starts with GZip magic
            byte[] written = File.ReadAllBytes(target);
            written.Length.Should().BeGreaterThan(2);
            written[0].Should().Be(0x1F);
            written[1].Should().Be(0x8B);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
