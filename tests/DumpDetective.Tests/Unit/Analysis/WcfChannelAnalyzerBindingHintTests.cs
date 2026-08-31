using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Enums;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class WcfChannelAnalyzerBindingHintTests
{
    // net.pipe channels share the same NamedPipeChannelFactory-derived nested type name, so
    // "NamedPipe" is checked first — realistic WCF client channel type names carry the
    // enclosing channel-factory name (e.g. "...NamedPipeChannelFactory+PipeConnectionChannel").
    [Theory]
    [InlineData("System.ServiceModel.Channels.NamedPipeChannelFactory+PipeConnectionChannel", WcfBindingHint.NamedPipe)]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", WcfBindingHint.NetTcp)]
    [InlineData("System.ServiceModel.Channels.SecurityChannelFactory+SecurityRequestSessionChannel", WcfBindingHint.WsHttp)]
    [InlineData("System.ServiceModel.Channels.HttpsChannelFactory+HttpsRequestChannel", WcfBindingHint.Basic)]
    [InlineData("System.ServiceModel.Channels.ServiceChannel", WcfBindingHint.Unknown)]
    public void ClassifyBindingHint_MapsTypeNameToExpectedHint(string typeName, WcfBindingHint expected)
    {
        WcfChannelAnalyzer.ClassifyBindingHint(typeName).Should().Be(expected);
    }

    [Fact]
    public void ClassifyBindingHint_PrefersTcpOverSecurity_WhenBothTokensPresent()
    {
        WcfChannelAnalyzer.ClassifyBindingHint("System.ServiceModel.Channels.SecurityTcpDuplexSessionChannel")
            .Should().Be(WcfBindingHint.NetTcp);
    }

    [Fact]
    public void ClassifyBindingHint_ReturnsUnknown_ForSharedFramingChannelWithNoBindingToken()
    {
        // Both net.tcp and net.pipe client channels can be a bare FramingDuplexSessionChannel
        // with no factory-name prefix on the heap — the heuristic can't disambiguate that case
        // and must not guess.
        WcfChannelAnalyzer.ClassifyBindingHint("System.ServiceModel.Channels.ClientFramingDuplexSessionChannel")
            .Should().Be(WcfBindingHint.Unknown);
    }
}
