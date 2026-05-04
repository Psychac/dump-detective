using DumpDetective.Cli.Commands;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Configuration;

using FluentAssertions;

using Xunit;

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

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.UsedConfigFile.Should().BeTrue();
            resolved.DumpPath.Should().Be("C:/dumps/from-config.dmp");
            resolved.MemoryLeak.HighReferenceThreshold.Should().Be(123);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldUseCliValue_WhenConfigMissingThatField()
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

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.MemoryLeak.MaxDuplicateStringLength.Should().Be(222);
            resolved.ReferenceChain.TopCount.Should().Be(6);
            resolved.Report.Format.Should().Be(ReportFormat.Text);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ShouldFallbackToCli_WhenConfigMissing()
    {
        AnalysisCommandRequest request = CreateRequest(configPath: null);
        ConfigurationResolver resolver = new();

        ResolvedExecutionOptions resolved = resolver.Resolve(request);

        resolved.UsedConfigFile.Should().BeTrue();
        resolved.DumpPath.Should().Be(request.DumpPath);
        resolved.MemoryLeak.HighReferenceThreshold.Should().Be(111);
        resolved.ReferenceChain.TopCount.Should().Be(6);
        resolved.EventLeak.MinSubscribers.Should().Be(3);
        resolved.Report.Format.Should().Be(ReportFormat.Html);
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenExplicitConfigPathMissing()
    {
        AnalysisCommandRequest request = CreateRequest(configPath: "C:/missing/does-not-exist.json");
        ConfigurationResolver resolver = new();

        Action act = () => resolver.Resolve(request);

        act.Should().Throw<FileNotFoundException>()
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
    public void Resolve_ShouldApplyAnalyzerProfileThenFieldOverrides_ForCrash()
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
                "Crash": {
                  "Profile": "Full",
                  "TopDetailedExceptionInstances": 7
                }
              }
            }
            """);

            AnalysisCommandRequest request = CreateRequest(configPath: configPath);
            ConfigurationResolver resolver = new();

            ResolvedExecutionOptions resolved = resolver.Resolve(request);

            resolved.Crash.TopDetailedExceptionInstances.Should().Be(7);
            resolved.Crash.MaxDetailedExceptionsPerType.Should().Be(10);
            resolved.Crash.MaxOriginalStackFramesToPrint.Should().Be(40);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
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

            resolved.Crash.MaxOriginalStackFramesToPrint.Should().Be(40);
            resolved.Collection.Profile.Should().Be(DumpDetective.Core.Options.AnalysisProfile.Full);
            resolved.Collection.PathAnalysisTopN.Should().Be(15);
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

            resolved.Crash.TopDetailedExceptionInstances.Should().Be(25);
            resolved.Collection.Profile.Should().Be(DumpDetective.Core.Options.AnalysisProfile.Balanced);
            resolved.Collection.PathAnalysisTopN.Should().Be(5);
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
                                "MemoryLeak": {
                                    "Profile": "Full",
                                    "MinDuplicateStringCount": 11
                                }
                            }
                        }
                        """);

                        AnalysisCommandRequest request = CreateRequest(configPath: configPath);
                        ConfigurationResolver resolver = new();

                        ResolvedExecutionOptions resolved = resolver.Resolve(request);

                        resolved.MemoryLeak.TopHighlyReferencedObjectsToShow.Should().Be(40);
                        resolved.MemoryLeak.MinDuplicateStringCount.Should().Be(11);
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

                        resolved.ReferenceChain.SearchMode.Should().Be(DumpDetective.Core.Options.ReferenceChainSearchMode.Fast);
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

                        resolved.EventLeak.IncludeNonLeakingEvents.Should().BeTrue();
                        resolved.EventLeak.TopDetailedInstancesPerGroup.Should().Be(20);
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
            ReferenceChainTopCount: 6,
            ReferenceChainMaxPathSearchObjects: 444,
            EventLeakMinSubscribers: 3,
            EnableMemoryDiagnostics: false,
            EnablePerformanceDiagnostics: true);
    }
}
