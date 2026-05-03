using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

using CoreAnalysisContext = DumpDetective.Core.Abstractions.AnalysisContext;

public sealed class AnalysisDiagnosticsTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldEmitNormalizedDiagnosticsEvents()
    {
        InMemoryAnalysisDiagnosticsSink sink = new();
        TestFindingGenerator generator = new();
        AnalysisPipeline pipeline = new([new MetricsAnalyzer()]);
        RuntimeAnalysisContext context = CreateContext(sink, continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);

        results.Should().HaveCount(1);
        AnalyzerRunResult run = results[0];
        run.FindingCount.Should().Be(0); // Pipeline sets 0; FindingGenerationPipeline enriches this in real runs
        run.WarningCount.Should().Be(1);
        run.ObjectScanCount.Should().Be(42);
        run.CacheHits.Should().Be(12);
        run.CacheMisses.Should().Be(3);

        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.RunStarted);
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerStarted && e.AnalyzerName == "MetricsAnalyzer");
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerCompleted && e.AnalyzerName == "MetricsAnalyzer");
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.RunCompleted);

        AnalysisDiagnosticsEvent completed = sink.Events.Single(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerCompleted);
        completed.DurationMs.Should().BeGreaterThan(0);
        completed.ObjectScanCount.Should().Be(42);
        completed.CacheHits.Should().Be(12);
        completed.CacheMisses.Should().Be(3);
        completed.TimestampUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotFail_WhenSinkThrows()
    {
        AnalysisPipeline pipeline = new([new MetricsAnalyzer()]);
        RuntimeAnalysisContext context = CreateContext(new ThrowingSink(), continueOnFailure: true);

        Func<Task> act = async () => await pipeline.ExecuteAsync(context, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static RuntimeAnalysisContext CreateContext(IAnalysisDiagnosticsSink sink, bool continueOnFailure)
    {
        DiagnosticsOptions diagnostics = new() { ContinueOnAnalyzerFailure = continueOnFailure };

        return new RuntimeAnalysisContext
        {
            Runtime = null!,
            Heap = null!,
            Cache = new HeapAnalysisCache(),
            Diagnostics = diagnostics,
            DiagnosticsSink = sink
        };
    }

    private sealed class MetricsAnalyzer : IAnalyzer
    {
        public string Name => "MetricsAnalyzer";
        public string Category => "Test";

        public ValueTask<AnalyzerDomainResult> AnalyzeAsync(CoreAnalysisContext context, CancellationToken cancellationToken)
        {
            GenericAnalyzerDomainResult result = new()
            {
                AnalyzerName = Name,
                Category = Category,
                Warnings = ["Synthetic warning"],
                Metrics = new Dictionary<string, object?>
                {
                    ["objectScans"] = 42,
                    ["cacheHits"] = 12,
                    ["cacheMisses"] = 3
                }
            };

            return ValueTask.FromResult<AnalyzerDomainResult>(result);
        }
    }

    private sealed class TestFindingGenerator : IFindingGenerator
    {
        public string AnalyzerName => "MetricsAnalyzer";
        public bool CanGenerate(AnalyzerDomainResult result) => true;
        public IReadOnlyList<InsightFinding> Generate(AnalyzerDomainResult result) =>
        [
            new InsightFinding(
                Analyzer: AnalyzerName,
                Category: "Test",
                Severity: FindingSeverity.Warning,
                Title: "Synthetic finding",
                Evidence: "Synthetic evidence",
                Recommendation: "Synthetic recommendation",
                Tags: ["test"])
        ];
    }

    private sealed class ThrowingSink : IAnalysisDiagnosticsSink
    {
        public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent) => throw new InvalidOperationException("Sink failure");
    }
}
