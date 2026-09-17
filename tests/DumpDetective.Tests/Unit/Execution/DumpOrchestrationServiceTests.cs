using DumpDetective.Analysis.Dump;
using DumpDetective.Analysis.Trend;
using DumpDetective.Cli.Execution;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Execution;

/// <summary>
/// Covers both the single-dump and trend shapes <see cref="DumpOrchestrationService"/> now
/// builds — merged from the former separate <c>TrendOrchestrationServiceTests</c> when migration
/// step 6 (docs/refactor/modularity/phase-4-session-orchestration.md) absorbed
/// <c>TrendOrchestrationService</c> into this class. Both shapes propagate a dump-load failure the
/// same way (before this merge, this was two near-identical tests against two separate classes).
/// </summary>
public sealed class DumpOrchestrationServiceTests
{
    private static DumpOrchestrationService CreateService(IDumpLoader dumpLoader) =>
        new(
            dumpLoader: dumpLoader,
            analyzerExecutionService: null!,
            reportBuilderFacade: null!,
            trendAnalyzer: new TrendAnalyzer(Array.Empty<IAnalyzerTrendComparer>()),
            outputWriter: null!,
            synthesisFindingEnricher: null!);

    [Fact]
    public async Task ExecuteAsync_SingleDump_PropagatesException_WhenDumpLoadFails()
    {
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        DumpOrchestrationService service = CreateService(new ThrowingDumpLoader());

        Func<Task> act = () => service.ExecuteAsync(
            resolved,
            allAnalyzers: Array.Empty<IAnalyzer>(),
            activeAnalyzers: Array.Empty<IAnalyzer>(),
            dumpPaths: [resolved.DumpPath],
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dump load failed");
    }

    [Fact]
    public async Task ExecuteAsync_Trend_PropagatesException_WhenDumpLoadFails()
    {
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        DumpOrchestrationService service = CreateService(new ThrowingDumpLoader());

        Func<Task> act = () => service.ExecuteAsync(
            resolved,
            allAnalyzers: Array.Empty<IAnalyzer>(),
            activeAnalyzers: Array.Empty<IAnalyzer>(),
            dumpPaths: ["a.dmp", "b.dmp"],
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("dump load failed");
    }

    private sealed class ThrowingDumpLoader : IDumpLoader
    {
        public Task<DumpLoadContext> LoadAsync(string dumpPath, CancellationToken cancellationToken, IProgress<DumpDetective.Core.Abstractions.AnalyzerProgressReport>? progress = null)
            => throw new InvalidOperationException("dump load failed");
    }
}
