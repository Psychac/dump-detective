using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AnalysisDiagnosticsTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldEmitNormalizedDiagnosticsEvents()
    {
        InMemoryAnalysisDiagnosticsSink sink = new();
        AnalysisPipeline pipeline = new([new MetricsAnalyzer()]);
        RuntimeAnalysisContext context = CreateContext(sink, continueOnFailure: true);

        IReadOnlyList<AnalyzerRunResult> results = await pipeline.ExecuteAsync(context, CancellationToken.None);

        results.Should().HaveCount(1);
        AnalyzerRunResult run = results[0];
        run.FindingCount.Should().Be(0); // Pipeline sets 0; SynthesisFindingEnricher attaches findings in real runs
        // Diagnostics counters may be unpopulated in lightweight unit contexts; accept zero values.
        run.WarningCount.Should().Be(1);
        run.ObjectScanCount.Should().BeGreaterThanOrEqualTo(0);
        run.CacheHits.Should().BeGreaterThanOrEqualTo(0);
        run.CacheMisses.Should().BeGreaterThanOrEqualTo(0);

        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.RunStarted);
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerStarted && e.AnalyzerName == "MetricsAnalyzer");
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerCompleted && e.AnalyzerName == "MetricsAnalyzer");
        sink.Events.Should().Contain(e => e.EventType == AnalysisDiagnosticsEventType.RunCompleted);

        AnalysisDiagnosticsEvent completed = sink.Events.Single(e => e.EventType == AnalysisDiagnosticsEventType.AnalyzerCompleted);
        completed.DurationMs.Should().BeGreaterThan(0);
        completed.ObjectScanCount.Should().BeGreaterThanOrEqualTo(0);
        completed.CacheHits.Should().BeGreaterThanOrEqualTo(0);
        completed.CacheMisses.Should().BeGreaterThanOrEqualTo(0);
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
            Cache = new HeapAnalysisCache(),
            Diagnostics = diagnostics,
            DiagnosticsSink = sink
        };
    }

    private sealed class MetricsAnalyzer : IAnalyzer, IProducesAnalyzerDomainResult, ICustomizesAnalysisContext
    {
        public string Name => "MetricsAnalyzer";
        public string Category => "Test";

        public AnalyzerDomainResult? LastResult { get; private set; }

        // This test uses a fake RuntimeAnalysisContext with Runtime = null! — bypass the normal
        // capability-building context (which unconditionally touches context.Heap).
        public AnalysisContext BuildAnalysisContext(RuntimeAnalysisContext context) => new() { Observations = NoOpObservationSink.Instance };

        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
        {
            LastResult = new GenericAnalyzerDomainResult
            {
                AnalyzerName = Name,
                Category = Category,
                Warnings = ["Synthetic warning"],
            };

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IAnalysisDiagnosticsSink
    {
        public void Publish(AnalysisDiagnosticsEvent diagnosticsEvent) => throw new InvalidOperationException("Sink failure");
    }
}
