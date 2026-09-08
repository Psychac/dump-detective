using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashFindingGeneratorTests
{
    private readonly CrashFindingGenerator _gen = new();

    [Fact]
    public void Generate_NoExceptions_EmitsInfoFinding()
    {
        var result = CrashResult(activeExceptions: 0, candidates: []) with { TotalExceptions = 0 };

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_NoActiveExceptions_EmitsWarningFinding()
    {
        var result = CrashResult(activeExceptions: 0, candidates: []);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_AllCandidatesExact_YieldsHighConfidenceCriticalFinding()
    {
        var result = CrashResult(activeExceptions: 3, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, InferenceConfidence.Exact)
        ]);

        var finding = _gen.Generate(result).Should().ContainSingle().Subject;

        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.ConfidenceScore.Should().BeApproximately(0.95, 0.0001);
    }

    [Fact]
    public void Generate_AllCandidatesNone_YieldsLowConfidenceCriticalFinding()
    {
        var result = CrashResult(activeExceptions: 2, candidates:
        [
            Candidate(threadId: 1, activeCount: 2, InferenceConfidence.None)
        ]);

        var finding = _gen.Generate(result).Should().ContainSingle().Subject;

        finding.Severity.Should().Be(FindingSeverity.Critical);
        finding.ConfidenceScore.Should().BeApproximately(0.15, 0.0001);
    }

    [Fact]
    public void Generate_MixedConfidenceTiers_WeightsByActiveExceptionCount()
    {
        var result = CrashResult(activeExceptions: 4, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, InferenceConfidence.Exact),
            Candidate(threadId: 2, activeCount: 1, InferenceConfidence.None),
        ]);

        var finding = _gen.Generate(result).Should().ContainSingle().Subject;

        // (0.95*3 + 0.15*1) / 4 = 0.75
        finding.ConfidenceScore.Should().BeApproximately(0.75, 0.0001);
        finding.EffectiveCaveats.Should().Contain(c => c.Contains("Exact") && c.Contains("None"));
    }

    private static CrashDomainResult CrashResult(int activeExceptions, IReadOnlyList<CrashThreadCandidateSnapshot> candidates) => new(
        TotalExceptions: activeExceptions + 10,
        ActiveExceptions: activeExceptions,
        ExceptionTypeCounts: new Dictionary<string, int> { ["FooException"] = activeExceptions + 10 },
        ActiveExceptionTypeCounts: new Dictionary<string, int> { ["FooException"] = activeExceptions },
        TopCrashThreadCandidates: candidates);

    private static CrashThreadCandidateSnapshot Candidate(uint threadId, int activeCount, InferenceConfidence confidence) => new(
        ThreadId: threadId,
        OSThreadId: threadId,
        ActiveExceptionCount: activeCount,
        PrimaryExceptionType: "FooException",
        TopFrames: [],
        OriginalStackTrace: null,
        OriginalStackTraceInferred: confidence != InferenceConfidence.Exact,
        OriginalStackTraceInferredFrom: null,
        OriginalStackTraceConfidence: confidence);
}
