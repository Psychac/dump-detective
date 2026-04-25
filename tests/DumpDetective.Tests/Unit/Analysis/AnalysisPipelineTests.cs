using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

using CoreAnalysisContext = DumpDetective.Core.Abstractions.AnalysisContext;
using PipelineAnalysisContext = DumpDetective.Analysis.Pipeline.AnalysisContext;

public sealed class AnalysisPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldContinueOnFailure_WhenConfigured()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Failing", 0, throwError: true),
            new TestAnalyzer("AfterFailure", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers);
        PipelineAnalysisContext context = CreateContext(continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Status.Should().Be(AnalyzerExecutionStatus.Failed);
        result[1].Status.Should().Be(AnalyzerExecutionStatus.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStopOnFailure_WhenConfigured()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Failing", 0, throwError: true),
            new TestAnalyzer("ShouldNotRun", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers);
        PipelineAnalysisContext context = CreateContext(continueOnFailure: false);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].AnalyzerName.Should().Be("Failing");
        result[0].Status.Should().Be(AnalyzerExecutionStatus.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMarkCanceled_WhenAnalyzerThrowsOperationCanceledException()
    {
        IAnalyzer[] analyzers =
        [
            new TestAnalyzer("Canceling", 0, throwCanceled: true),
            new TestAnalyzer("ShouldNotRun", 1)
        ];

        AnalysisPipeline pipeline = new(analyzers);
        PipelineAnalysisContext context = CreateContext(continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> result = await pipeline.ExecuteAsync(context, CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(AnalyzerExecutionStatus.Canceled);
    }

    private static PipelineAnalysisContext CreateContext(bool continueOnFailure)
    {
        DiagnosticsOptions diagnostics = new()
        {
            ContinueOnAnalyzerFailure = continueOnFailure
        };

        return new PipelineAnalysisContext
        {
            Runtime = null!,
            Heap = null!,
            Cache = new HeapAnalysisCache(),
            Diagnostics = diagnostics,
            DiagnosticsOptions = diagnostics,
            MemoryLeakOptions = new MemoryLeakOptions(),
            ReferenceChainOptions = new ReferenceChainOptions(),
            EventLeakOptions = new EventLeakOptions()
        };
    }

    private sealed class TestAnalyzer(string name, int order, Action? onExecute = null, bool throwError = false, bool throwCanceled = false) : IAnalyzer
    {
        public string Name { get; } = name;
        public int Order { get; } = order;
        public string Category => "Test";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(CoreAnalysisContext context, CancellationToken cancellationToken)
        {
            onExecute?.Invoke();

            if (throwCanceled)
            {
                throw new OperationCanceledException("Canceled by test analyzer.");
            }

            if (throwError)
            {
                throw new InvalidOperationException("Failure from test analyzer.");
            }

            AnalyzerDomainResult result = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category
            };

            return ValueTask.FromResult(result);
        }
    }
}
