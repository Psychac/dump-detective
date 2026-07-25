using DumpDetective.Analysis.Analyzers;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class TypeNamePatternMatcherTests
{
    [Theory]
    [InlineData("System.Threading.Tasks.Task", true)]
    [InlineData("System.Threading.Tasks.Task`1", true)]
    [InlineData("System.Threading.Timer", false)]
    public void HasAnyPrefix_MatchesAnyPrefix(string typeName, bool expected)
    {
        TypeNamePatternMatcher.HasAnyPrefix(typeName, ["System.Threading.Tasks.Task"]).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.Net.Http.HttpMessageHandler", true)]
    [InlineData("MyApp.HttpMessageHandlerWrapper", true)]
    [InlineData("System.Net.Http.HttpClient", false)]
    public void ContainsAny_MatchesAnyToken(string typeName, bool expected)
    {
        TypeNamePatternMatcher.ContainsAny(typeName, ["HttpMessageHandler"]).Should().Be(expected);
    }

    [Theory]
    [InlineData("Npgsql.NpgsqlConnection", true)]
    [InlineData("Npgsql.NpgsqlCommand", false)]
    [InlineData("MyApp.CustomConnection", false)]
    public void HasPrefixAndSuffixOrContains_PrefixAndSuffix(string typeName, bool expected)
    {
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(typeName, ["Npgsql.", "System.Data.SqlClient."], "Connection", null)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("System.ServiceModel.Channels.ServiceChannel", true)]
    [InlineData("System.ServiceModel.ClientBase`1", true)]
    [InlineData("System.ServiceModel.SomeUnrelatedType", false)]
    [InlineData("MyApp.Channel", false)]
    public void HasPrefixAndSuffixOrContains_PrefixAndContainsAny(string typeName, bool expected)
    {
        TypeNamePatternMatcher.HasPrefixAndSuffixOrContains(
            typeName, ["System.ServiceModel."], ".ServiceChannel", ["Channel", "ClientBase", "CommunicationObject"])
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Int32, mscorlib]]", "Dictionary")]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary`2+Node", "ConcurrentDictionary")]
    [InlineData("System.Collections.Generic.List<System.String>", "List")]
    [InlineData("MyApp.Namespace.PlainType", "PlainType")]
    [InlineData("PlainType", "PlainType")]
    public void GetShortName_StripsGenericAndNestedNoise(string typeName, string expected)
    {
        TypeNamePatternMatcher.GetShortName(typeName).Should().Be(expected);
    }
}
