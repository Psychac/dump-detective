using DumpDetective.Analysis.Dump;
using DumpDetective.Cli.Execution;
using DumpDetective.Cli.Output;
using DumpDetective.Cli.Services;
using DumpDetective.Core.Abstractions;
using DumpDetective.Reporting.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Execution;

public sealed class SingleDumpOrchestrationServiceTests
{
    [Fact]
    public async Task ExecuteAsync_PropagatesException_WhenDumpLoadFails()
    {
        var resolved = ResolvedExecutionOptionsFactory.Create("out.json");
        var stageFactory = new SingleDumpStageFactory(
            dumpLoader: new ThrowingDumpLoader(),
            analyzerExecutionService: null!,
            reportBuilderFacade: null!,
            outputWriter: null!);
        var service = new SingleDumpOrchestrationService(stageFactory);

        Func<Task> act = () => service.ExecuteAsync(
            resolved,
            allAnalyzers: Array.Empty<IAnalyzer>(),
            activeAnalyzers: Array.Empty<IAnalyzer>(),
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
