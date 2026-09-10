using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Models;

using FluentAssertions;

using Xunit;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Unit.Configuration;

public sealed class ConfigurationResolverTests
{
    [Fact]
    public void Resolve_ShouldUseDumpPathFromConfig()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with { DumpPath = null, OutputFormat = ReportFormat.Html };
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.UsedConfigFile.Should().BeTrue();
            resolved.DumpPath.Should().Be("C:/dumps/from-config.dmp");
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldHonorReportStyleVersion_FromConfig()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "ReportStyleVersion": "v2"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with
            {
                DumpPath = null,
                OutputFormat = ReportFormat.Html,
                ReportStyleVersion = ReportStyleVersion.V1
            };
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Report.StyleVersion.Should().Be(ReportStyleVersion.V2);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldNotLetPartialReportObjectShadowTopLevelReportStyle()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "ReportStyleVersion": "v2",
              "Report": {
                "Format": "Html"
              }
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with
            {
                DumpPath = null,
                OutputFormat = ReportFormat.Html,
                ReportStyleVersion = ReportStyleVersion.V1,
                PreRender = true,
                SeparateJson = true
            };
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Report.StyleVersion.Should().Be(ReportStyleVersion.V2);
            resolved.Report.PreRender.Should().BeTrue();
            resolved.Report.SeparateJson.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldUseCliReportStyle_WhenConfigMissingStyle()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with
            {
                DumpPath = null,
                OutputFormat = ReportFormat.Html,
                ReportStyleVersion = ReportStyleVersion.V2
            };
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Report.StyleVersion.Should().Be(ReportStyleVersion.V2);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenExplicitConfigPathMissing()
    {
        AnalysisCommandRequest request = CreateRequest(configPath: "C:/missing/does-not-exist.json");
        ConfigurationResolver resolver = new();

        Action act = () => resolver.Resolve(request);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.InnerException is FileNotFoundException)
            .WithMessage("*does-not-exist.json*");
    }

    [Fact]
    public void Resolve_ShouldUseLastTrendDump_AsEffectiveDump_WhenOnlyTrendProvidedFromCli()
    {
        AnalysisCommandRequest request = CreateRequest(configPath: null) with
        {
            DumpPath = null,
            TrendDumpPaths = ["C:/dumps/t1.dmp", "C:/dumps/t2.dmp", "C:/dumps/t3.dmp"]
        };

        ConfigurationResolver resolver = new();

        ResolvedExecutionOptions resolved = resolver.Resolve(request);

        resolved.DumpPath.Should().Be("C:/dumps/t3.dmp");
        resolved.TrendDumpPaths.Should().Equal("C:/dumps/t1.dmp", "C:/dumps/t2.dmp", "C:/dumps/t3.dmp");
    }

    // Per-analyzer configurability (the AnalysisProfile tier system, and later every individual
    // analyzer threshold/cap under "Analyzers"/"ExecutionPolicy") has been removed entirely — see
    // docs/refactor/analysis-options-removal-plan.md. These tests confirm dead/legacy keys are
    // inert (resolution succeeds regardless of their presence or value) rather than causing a
    // resolution failure or silently doing something.

    [Fact]
    public void Resolve_ShouldNotThrow_WhenLegacyProfileKeyIsAnInvalidValue()
    {
        // Previously "Profile": "not-a-real-tier" threw ArgumentException during resolution.
        // The key is now inert — resolution must succeed regardless of its value.
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "Profile": "not-a-real-tier"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            Action act = () => resolver.Resolve(request);

            act.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldNotThrow_WhenConfigContainsDeadAnalyzersOrExecutionPolicySections()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "Analyzers": {
                "String": { "MinDuplicateStringCount": 11 }
              },
              "ExecutionPolicy": {
                "MaxLeakScanObjects": 999
              }
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            Action act = () => resolver.Resolve(request);

            act.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"dumpdetective-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private static AnalysisCommandRequest CreateRequest(string? configPath)
    {
        return new AnalysisCommandRequest(
            DumpPath: "C:/dumps/from-cli.dmp",
            OutputPath: null,
            OutputFormat: ReportFormat.Text,
            ConfigPath: configPath,
            IncludeAnalyzers: Array.Empty<string>(),
            ExcludeAnalyzers: Array.Empty<string>(),
            DiagnosticMode: false,
            BaselineDumpPath: null,
            TrendDumpPaths: null,
            EnableMemoryDiagnostics: false,
            EnablePerformanceDiagnostics: true);
    }
}
