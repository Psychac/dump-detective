using DumpDetective.Cli.Execution;

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
}
