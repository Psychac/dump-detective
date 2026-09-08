using DumpDetective.Analysis.Cache;
using DumpDetective.Core.Abstractions;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class ObjectScanCounterTests
{
    // System.Progress<T> posts callbacks via the captured SynchronizationContext (ThreadPool by
    // default in a test host), so it never invokes synchronously - a synchronous IProgress<T> is
    // needed to assert against the report on the same thread.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        public T? Last;
        public void Report(T value) => Last = value;
    }

    [Fact]
    public void Report_WithTotal_PrefixesDetailWithPercent()
    {
        var progress = new SynchronousProgress<AnalyzerProgressReport>();
        var counter = new ObjectScanCounter("scanning", progress, total: 200);

        for (int i = 0; i < 50; i++)
            counter.ShouldReport();
        counter.Report("42 wasteful");

        progress.Last.Should().NotBeNull();
        progress.Last!.Detail.Should().Be("25% · 42 wasteful");
    }

    [Fact]
    public void Report_WithTotal_NoExtraDetail_ReportsPercentOnly()
    {
        var progress = new SynchronousProgress<AnalyzerProgressReport>();
        var counter = new ObjectScanCounter("scanning", progress, total: 100);

        for (int i = 0; i < 10; i++)
            counter.ShouldReport();
        counter.Report();

        progress.Last!.Detail.Should().Be("10%");
    }

    [Fact]
    public void Report_WithoutTotal_LeavesDetailUnchanged()
    {
        var progress = new SynchronousProgress<AnalyzerProgressReport>();
        var counter = new ObjectScanCounter("scanning", progress);

        counter.Report("raw detail");

        progress.Last!.Detail.Should().Be("raw detail");
    }

    [Fact]
    public void Report_ScannedExceedsTotal_ClampsPercentAt100()
    {
        var progress = new SynchronousProgress<AnalyzerProgressReport>();
        // A stale/undercounted total hint (e.g. index header predates late-added objects) must
        // never surface a nonsensical percentage above 100%.
        var counter = new ObjectScanCounter("scanning", progress, total: 10);

        for (int i = 0; i < 50; i++)
            counter.ShouldReport();
        counter.Report();

        progress.Last!.Detail.Should().Be("100%");
    }

    [Fact]
    public void Report_ZeroOrNegativeTotal_TreatedAsNoHint()
    {
        var progress = new SynchronousProgress<AnalyzerProgressReport>();
        var counter = new ObjectScanCounter("scanning", progress, total: 0);

        counter.Report("raw detail");

        progress.Last!.Detail.Should().Be("raw detail");
    }
}
