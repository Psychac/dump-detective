using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Configuration;
using DumpDetective.Cli.Diagnostics;
using DumpDetective.Cli.Services;
using DumpDetective.Cli.Models;
using DumpDetective.Core.Configuration;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;
using DumpDetective.Core.Enums;

namespace DumpDetective.Tests.Unit.Configuration;

public sealed class ConfigurationResolverTests
{
    [Fact]
    public void Resolve_ShouldUseConfigValues_WhenConfigProvidesField()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "MemoryLeak": {
                "HighReferenceThreshold": 123
              }
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with { DumpPath = null, OutputFormat = ReportFormat.Html };
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.UsedConfigFile.Should().BeTrue();
            resolved.MemoryLeak.HighReferenceThreshold.Should().Be(123);
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
    public void Resolve_ShouldUseProfileBaseline_WhenConfigMissingThatField()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "MemoryLeak": {
                "HighReferenceThreshold": 123
              }
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with { DumpPath = null, OutputFormat = ReportFormat.Html };
            ConfigurationResolver resolver = new();
            RetentionOptions balancedMemoryLeak = new RetentionOptions();
            ReferenceChainOptions balancedReferenceChain = new ReferenceChainOptions();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            var balancedString = new StringAnalysisOptions();
            resolved.StringAnalysis.MaxDuplicateStringLength.Should().Be(balancedString.MaxDuplicateStringLength);
            resolved.ReferenceChain.TopCount.Should().Be(balancedReferenceChain.TopCount);
            resolved.Report.Format.Should().Be(ReportFormat.Html);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldUseProfileBaseline_WhenConfigMissing()
    {
        // Create an explicit minimal config file to avoid reliance on sample files
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, "{ \"DumpPath\": \"C:/dumps/from-config.dmp\" }");

            AnalysisCommandRequest request = CreateRequest(configPath: configPath) with { OutputFormat = ReportFormat.Html };
            ConfigurationResolver resolver = new();
            RetentionOptions balancedMemoryLeak = new RetentionOptions();
            ReferenceChainOptions balancedReferenceChain = new ReferenceChainOptions();
            EventLeakOptions balancedEventLeak = new EventLeakOptions();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.UsedConfigFile.Should().BeTrue();
            resolved.DumpPath.Should().Be("C:/dumps/from-config.dmp");
            resolved.MemoryLeak.HighReferenceThreshold.Should().Be(balancedMemoryLeak.HighReferenceThreshold);
            resolved.ReferenceChain.TopCount.Should().Be(balancedReferenceChain.TopCount);
            resolved.EventLeak.TopDetailedInstancesPerGroup.Should().Be(balancedEventLeak.TopDetailedInstancesPerGroup);
            resolved.Report.Format.Should().Be(ReportFormat.Html);
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

    [Fact]
    public void Resolve_ShouldMapDeepToFull_ForGlobalProfile()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "Profile": "Deep"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Collection.PathAnalysisTopN.Should().Be(5);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldFallbackToBalancedProfile_WhenNoProfileProvided()
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

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Collection.PathAnalysisTopN.Should().Be(5);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldApplyGlobalProfileBaseline_WhenAnalyzerSectionsMissing()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "Profile": "Full"
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.MemoryLeak.TopHighlyReferencedObjectsToShow.Should().Be(15);
            resolved.MemoryLeak.MaxLeakScanObjects.Should().Be(2_000_000);

            resolved.ReferenceChain.MaxRootExpansionDepth.Should().Be(12);

            resolved.EventLeak.TopDetailedInstancesPerGroup.Should().Be(5);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldIgnoreLegacyAliasFields_AndUseUniformAnalyzerFlow()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
            {
              "DumpPath": "C:/dumps/from-config.dmp",
              "Profile": "Full",
              "HighReferenceThreshold": 123,
              "ReferenceChainTopCount": 9,
              "EventLeakMinSubscribers": 4
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.MemoryLeak.HighReferenceThreshold.Should().Be(50);
            resolved.MemoryLeak.TopHighlyReferencedObjectsToShow.Should().Be(15);

            resolved.ReferenceChain.TopCount.Should().Be(10);

            resolved.EventLeak.TopDetailedInstancesPerGroup.Should().Be(5);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldApplyAnalyzerProfileThenFieldOverrides_ForMemoryLeak()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
                        {
                            "DumpPath": "C:/dumps/from-config.dmp",
                            "Profile": "Fast",
                            "Analyzers": {
                                "String": {
                                    "Profile": "Full",
                                    "MinDuplicateStringCount": 11
                                }
                            }
                        }
                        """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.MemoryLeak.TopHighlyReferencedObjectsToShow.Should().Be(15);
            resolved.StringAnalysis.MinDuplicateStringCount.Should().Be(11);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldUseGlobalProfile_WhenAnalyzerProfileMissing_ForReferenceChain()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
                        {
                            "DumpPath": "C:/dumps/from-config.dmp",
                            "Profile": "Fast",
                            "Analyzers": {
                                "ReferenceChain": {
                                    "TopCount": 9
                                }
                            }
                        }
                        """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.ReferenceChain.TopCount.Should().Be(9);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldApplyAnalyzerProfile_ForEventLeak()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(tempDirectory, "config.json");
            File.WriteAllText(configPath, """
                        {
                            "DumpPath": "C:/dumps/from-config.dmp",
                            "Profile": "Fast",
                            "Analyzers": {
                                "EventLeak": {
                                    "Profile": "Full"
                                }
                            }
                        }
                        """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.EventLeak.TopDetailedInstancesPerGroup.Should().Be(5);
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
            HighReferenceThreshold: 111,
            MaxDuplicateStringLength: 222,
            MinDuplicateStringCount: 9,
            MaxReferenceAddresses: 333,
            EnableMemoryDiagnostics: false,
            EnablePerformanceDiagnostics: true);
    }
}
