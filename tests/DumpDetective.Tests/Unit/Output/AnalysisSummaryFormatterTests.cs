using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Cli.Models;
using DumpDetective.Cli.Output;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Output;

public sealed class AnalysisSummaryFormatterTests
{
    [Fact]
    public void FormatConfigSummary_ShouldMatchSingleAndTrendConsoleFormat()
    {
        ResolvedExecutionOptions resolved = ResolvedExecutionOptionsFactory.Create("C:/dumps/report.html") with
        {
            UsedConfigFile = true,
            ConfigPath = "C:/configs/config.json"
        };

        string summary = AnalysisSummaryFormatter.FormatConfigSummary(resolved, [new TestAnalyzer("LeakAnalyzer"), new TestAnalyzer("ThreadAnalyzer")]);

        summary.Should().Be("Config: file (C:/configs/config.json)  ·  2 analyzers: LeakAnalyzer, ThreadAnalyzer");
    }

    private sealed class TestAnalyzer(string name) : IAnalyzer, IProducesAnalyzerDomainResult
    {
        public string Name { get; } = name;
        public string Category => "Test";

        public AnalyzerDomainResult? LastResult { get; private set; }

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            LastResult = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = "Test"
            };

            return ValueTask.CompletedTask;
        }
    }
}
