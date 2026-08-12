using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Execution;

public sealed class TrendOrchestrationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_PropagatesException_WhenDumpLoadFails()
    {
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        var perDumpExecutionService = new PerDumpExecutionService(
            dumpLoader: new ThrowingDumpLoader(),
            analyzerExecutionService: null!);

        var service = new TrendOrchestrationService(
            reportBuilderFacade: null!,
            trendAnalyzer: new TrendAnalyzer(Array.Empty<IAnalyzerTrendComparer>()),
            outputWriter: null!,
            perDumpExecutionService: perDumpExecutionService);

        Func<Task> act = () => service.ExecuteAsync(
            resolved,
            allAnalyzers: Array.Empty<IAnalyzer>(),
            activeAnalyzers: Array.Empty<IAnalyzer>(),
            trendDumpPaths: new[] { "a.dmp", "b.dmp" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dump load failed");
    }

    private sealed class ThrowingDumpLoader : IDumpLoader
    {
        public Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken, IProgress<AnalyzerProgressReport>? progress = null)
            => throw new InvalidOperationException("dump load failed");
    }
}
