using DumpDetective.Cli.Services;
using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Configuration;

public sealed class StartupValidatorTests
{
    [Fact]
    public void Validate_ShouldThrow_WhenTrendDumpPathsContainsOnlyOnePath()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string dumpPath = CreateTempDump(tempDirectory, "current.dmp");
            string trendPath = CreateTempDump(tempDirectory, "trend-1.dmp");

            ResolvedExecutionOptions options = CreateOptions(
                dumpPath: dumpPath,
                trendDumpPaths: [trendPath]);

            StartupValidator validator = new();

            Action act = () => validator.Validate(options);

            act.Should().Throw<ArgumentException>()
                .WithMessage("*TrendDumpPaths must contain at least two dump paths.*");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Validate_ShouldNotThrow_WhenTrendDumpPathsContainsTwoPaths()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string dumpPath = CreateTempDump(tempDirectory, "current.dmp");
            string trendPath1 = CreateTempDump(tempDirectory, "trend-1.dmp");
            string trendPath2 = CreateTempDump(tempDirectory, "trend-2.dmp");

            ResolvedExecutionOptions options = CreateOptions(
                dumpPath: dumpPath,
                trendDumpPaths: [trendPath1, trendPath2]);

            StartupValidator validator = new();

            Action act = () => validator.Validate(options);

            act.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static ResolvedExecutionOptions CreateOptions(string dumpPath, IReadOnlyList<string>? trendDumpPaths)
    {
        return new ResolvedExecutionOptions(
            DumpPath: dumpPath,
            OutputPath: Path.ChangeExtension(dumpPath, ".txt")!,
            BaselineDumpPath: null,
            TrendDumpPaths: trendDumpPaths,
            MemoryLeak: new MemoryLeakOptions(),
            ReferenceChain: new ReferenceChainOptions(),
            EventLeak: new EventLeakOptions(),
            Diagnostics: new DiagnosticsOptions(),
            Report: new ReportOptions { Format = ReportFormat.Text },
            Collection: new CollectionAnalysisOptions(),
            ConfigPath: null,
            UsedConfigFile: false,
            IncludeAnalyzers: Array.Empty<string>(),
            ExcludeAnalyzers: Array.Empty<string>(),
            DiagnosticMode: false,
            IndexPrebuildMode: HeapIndexPrebuildMode.Auto);
    }

    private static string CreateTempDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"dumpdetective-validator-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static string CreateTempDump(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "dummy");
        return path;
    }
}
