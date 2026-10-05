using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

public sealed class ClaimEvidenceTests
{
    [Fact]
    public void Counter_None_RecordsWhatWasConsidered_AndHasNoStatements()
    {
        Counter none = Counter.None("checked dominant roots; all are bounded caches");

        none.Statements.Should().BeEmpty();
        none.NoneFoundBecause.Should().Be("checked dominant roots; all are bounded caches");
    }

    [Fact]
    public void Counter_Of_CarriesStatementsAndNoNoneReason()
    {
        var statement = new EvidenceStatement([ObservationId.NewId()], "churn, not retention");

        Counter counter = Counter.Of(statement);

        counter.Statements.Should().ContainSingle().Which.Should().Be(statement);
        counter.NoneFoundBecause.Should().BeNull();
    }

    [Fact]
    public void Finding_NotYetMigrated_HasNoCounterAtAll_SoItIsDistinguishableFromExplicitNone()
    {
        Finding finding = new()
        {
            Fingerprint = "fp",
            Severity = Severity.Info,
            Title = "t",
            Narrative = "n",
            Recommendation = "r",
            DerivedFrom = [],
            Confidence = ConfidenceBreakdown.MeasurementCertain,
            Analyzer = "a",
            Category = "c",
        };

        finding.Counter.Should().BeNull();
        finding.Assertion.Should().BeNull();
        finding.FalsifiedBy.Should().BeEmpty();
        (finding with { Counter = Counter.None("looked") }).Counter.Should().NotBeNull();
    }
}
