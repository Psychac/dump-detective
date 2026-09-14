using DumpDetective.Analysis.Analyzers.Infra;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Tier-2 wave retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="WcfChannelAnalyzer.CreateWorkerInstance"/>/<c>MergePartial</c> are now
/// pure generic base-class behavior (<c>TypedResourceCandidateStreaming</c>), already
/// covered once by <c>SqlConnectionPoolAnalyzerHeapIndexScanTests</c> — re-testing the identical
/// generic logic per adapter would be pure duplication. Covers the adapter-specific
/// <see cref="WcfChannelAnalyzer.IsCandidateType"/> wiring plus the classification
/// helpers that stayed unchanged on <see cref="WcfChannelAnalyzer"/> through the retyping.
/// </summary>
public sealed class WcfChannelAnalyzerHeapIndexScanTests
{
    [Theory]
    [InlineData("System.ServiceModel.Channels.ServiceChannel", true)]
    [InlineData("System.ServiceModel.ClientBase`1", true)]
    [InlineData("System.ServiceModel.Channels.CommunicationObject", true)]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory", false)] // factory, not a channel
    [InlineData("Microsoft.Data.SqlClient.SqlCommand", false)]
    public void IsCandidateType_MatchesWcfChannelNamespaceAndSuffixOrTokens(string typeName, bool expected)
    {
        WcfChannelAnalyzer adapter = new();

        ((ITypedResourceCandidateSource)adapter).IsCandidateType(typeName).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory", true)]
    [InlineData("System.ServiceModel.Channels.NamedPipeChannelFactory", true)]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", false)] // channel, not a factory
    public void IsFactoryType_MatchesWcfChannelFactoryNamespaceAndSuffixOrTokens(string typeName, bool expected)
    {
        WcfChannelAnalyzer.IsFactoryType(typeName).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]  // Created
    [InlineData(1, true)]  // Opening
    [InlineData(5, true)]  // Faulted
    [InlineData(-1, false)]
    [InlineData(6, false)]
    [InlineData(int.MaxValue, false)]
    public void IsValidCommunicationState_MatchesTheDefinedEnumRange(int stateVal, bool expected)
    {
        WcfChannelAnalyzer.IsValidCommunicationState(stateVal).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.HttpsChannelFactory+HttpsRequestChannel", false)]
    public void IsDuplexChannelType_MatchesDuplexToken(string typeName, bool expected)
    {
        WcfChannelAnalyzer.IsDuplexChannelType(typeName).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.TcpChannelFactory+ClientFramingDuplexSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.SecurityChannelFactory+SecurityRequestSessionChannel", true)]
    [InlineData("System.ServiceModel.Channels.HttpsChannelFactory+HttpsRequestChannel", false)]
    public void IsSessionChannelType_MatchesSessionToken(string typeName, bool expected)
    {
        WcfChannelAnalyzer.IsSessionChannelType(typeName).Should().Be(expected);
    }
}
