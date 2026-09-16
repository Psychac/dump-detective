using DumpDetective.Cli.Execution;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Execution;

/// <summary>
/// Covers the extension-sniffed trace/dump routing decision
/// (docs/refactor/modularity-plan.md § 8's "interim router") in isolation — standing up
/// <c>DumpAnalysisService</c>'s full nine-dependency constructor just to exercise a two-line
/// branch would be disproportionate, so this targets <see cref="DumpAnalysisService.IsTraceFile"/>
/// directly.
/// </summary>
public sealed class DumpAnalysisServiceRoutingTests
{
    [Theory]
    [InlineData("capture.etl")]
    [InlineData("capture.ETL")]
    [InlineData("capture.nettrace")]
    [InlineData(@"D:\Dumps\08-05\etls\HighCPU_11.etl")]
    public void IsTraceFile_TraceExtensions_ReturnsTrue(string path)
    {
        DumpAnalysisService.IsTraceFile(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("crash.dmp")]
    [InlineData("crash.DMP")]
    [InlineData("no-extension")]
    [InlineData(null)]
    public void IsTraceFile_NonTraceOrMissingPath_ReturnsFalse(string? path)
    {
        DumpAnalysisService.IsTraceFile(path).Should().BeFalse();
    }

    /// <summary>
    /// Covers <see cref="DumpAnalysisService.FilterToDumpCatalog"/> in isolation — same
    /// disproportionate-DI-wiring rationale as <see cref="IsTraceFile"/> above. This is the one
    /// piece of migration step 5d's combined-session logic worth a dedicated unit test: correctness
    /// here is what keeps a trace analyzer from ever reaching the dump-only <c>AnalysisPipeline</c>.
    /// </summary>
    public sealed class FilterToDumpCatalogTests
    {
        [Fact]
        public void ReturnsOnlyInstancesFromTheDumpCatalog_ByReference()
        {
            var dumpAnalyzer1 = new FakeAnalyzer("Dump1");
            var dumpAnalyzer2 = new FakeAnalyzer("Dump2");
            var traceAnalyzer = new FakeAnalyzer("Trace1");
            IReadOnlyList<IAnalyzer> dumpCatalog = [dumpAnalyzer1, dumpAnalyzer2];
            IReadOnlyList<IAnalyzer> satisfied = [dumpAnalyzer2, traceAnalyzer];

            IReadOnlyList<IAnalyzer> result = DumpAnalysisService.FilterToDumpCatalog(satisfied, dumpCatalog);

            result.Should().ContainSingle().Which.Should().BeSameAs(dumpAnalyzer2);
        }

        [Fact]
        public void DoesNotMatchOnName_OnlyOnReference()
        {
            // Two distinct instances that happen to share a Name — reference equality must not be
            // fooled into treating them as the same analyzer.
            var dumpAnalyzer = new FakeAnalyzer("SameName");
            var traceAnalyzerWithSameName = new FakeAnalyzer("SameName");
            IReadOnlyList<IAnalyzer> dumpCatalog = [dumpAnalyzer];
            IReadOnlyList<IAnalyzer> satisfied = [traceAnalyzerWithSameName];

            IReadOnlyList<IAnalyzer> result = DumpAnalysisService.FilterToDumpCatalog(satisfied, dumpCatalog);

            result.Should().BeEmpty();
        }

        private sealed class FakeAnalyzer(string name) : IAnalyzer
        {
            public string Name => name;
            public string Category => "Test";
            public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        }
    }
}
