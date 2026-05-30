using DumpDetective.Cli;
using DumpDetective.Cli.Services;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration;

public sealed class ProgramEntryPointTests
{
    [Fact]
    public async Task Main_ShouldReturnParseErrorCode_ForUnknownOption()
    {
        int exitCode = await Program.Main(["--definitely-invalid-option"]);

        exitCode.Should().Be(ExitCodes.ConfigurationFailure);
    }

    [Fact]
    public async Task Main_ShouldReturnConfigurationFailure_ForInvalidReportFormat()
    {
        int exitCode = await Program.Main(["--report-format", "not-a-real-format"]);

        exitCode.Should().Be(ExitCodes.ConfigurationFailure);
    }
}
