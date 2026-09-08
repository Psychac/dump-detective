using System.Text.RegularExpressions;

using DumpDetective.Analysis.Utilities;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class AsyncStateMachineRegexTests
{
    // Tests the actual shared production regex (DumpDetective.Analysis.Utilities.AsyncStateMachineNamePattern),
    // not a private copy — this is what caught the Phase 1/Phase 2 drift bug (P0-4): a disconnected
    // fourth copy of this pattern previously lived here and validated itself, not production code.
    private static readonly Regex StateMachinePattern = AsyncStateMachineNamePattern.Regex;

    [Theory]
    [InlineData("<Method>d__1", "Method")]
    [InlineData("<MyAsyncMethod>d__42", "MyAsyncMethod")]
    [InlineData("<GetData>d__5", "GetData")]
    public void Pattern_MatchesNonGenericAsyncStateMachines(string typeName, string expectedMethod)
    {
        var match = StateMachinePattern.Match(typeName);

        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be(expectedMethod);
    }

    [Theory]
    [InlineData("<GenericMethod>d__1[[T]]", "GenericMethod")]
    [InlineData("<GenericMethod>d__1[[System.String, mscorlib]]", "GenericMethod")]
    [InlineData("<GetAsync>d__5[[T]]", "GetAsync")]
    [InlineData("<ProcessData>d__42[[System.Collections.Generic.List`1[[System.String, mscorlib]], System.Collections]]", "ProcessData")]
    public void Pattern_MatchesGenericAsyncStateMachines(string typeName, string expectedMethod)
    {
        var match = StateMachinePattern.Match(typeName);

        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be(expectedMethod);
    }

    [Theory]
    [InlineData("<Method>d__1[[T, U]]")]
    [InlineData("<GenericMethod>d__99[[System.String, mscorlib]][[System.Int32, mscorlib]]")]
    public void Pattern_MatchesComplexGenericAsyncStateMachines(string typeName)
    {
        var match = StateMachinePattern.Match(typeName);

        match.Success.Should().BeTrue();
    }

    [Theory]
    [InlineData("Method>d__1")]      // Missing opening angle bracket
    [InlineData("<Method>d__")]      // Missing number
    [InlineData("<Method>d__1X")]    // Extra character after number
    [InlineData("Method")]           // Not a state machine at all
    [InlineData("<MethodName>")]     // Missing d__N suffix
    public void Pattern_RejectsNonStateMatching_Names(string typeName)
    {
        var match = StateMachinePattern.Match(typeName);

        match.Success.Should().BeFalse();
    }

    [Fact]
    public void Pattern_ExtractsMethodNameWithNestedGenerics()
    {
        string complexGenericType =
            "<QueryAsync>d__7[[System.Collections.Generic.IEnumerable`1[[System.String, mscorlib]], System.Linq]]";

        var match = StateMachinePattern.Match(complexGenericType);

        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be("QueryAsync");
    }

    [Fact]
    public void Pattern_DoesNotMatchIteratorStateMachines()
    {
        // C# iterators (yield return) also use d__N pattern but should not be async state machines
        // This test documents that the regex matches them; the actual analyzer filters by IAsyncStateMachine interface
        string iteratorType = "<Iterator>d__1";

        var match = StateMachinePattern.Match(iteratorType);

        match.Success.Should().BeTrue();  // Regex matches, but analyzer will reject via interface check
    }

    [Theory]
    [InlineData("<MixedCase>d__1")]
    [InlineData("<ALLCAPS>d__999")]
    [InlineData("<snake_case>d__5")]
    [InlineData("<method.with.dots>d__1")]
    public void Pattern_HandlesVaryingMethodNameStyles(string typeName)
    {
        var match = StateMachinePattern.Match(typeName);

        match.Success.Should().BeTrue();
    }

    [Fact]
    public void Pattern_TimeoutTolerance()
    {
        // Test that regex with 50ms timeout doesn't timeout on complex inputs
        string complexInput = "<VeryLongMethodNameWith>d__1[[System.Collections.Generic.Dictionary`2[[System.String, mscorlib], System.Tuple`3[[System.Int32, mscorlib], System.String, System.Object]], System.Collections]]";

        var match = StateMachinePattern.Match(complexInput);

        match.Success.Should().BeTrue();
    }
}
