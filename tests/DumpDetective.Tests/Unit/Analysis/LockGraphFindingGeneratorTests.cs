using DumpDetective.Analysis.Models;
using DumpDetective.Core.Enums;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.FindingGenerators;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class LockGraphFindingGeneratorTests
{
    private readonly LockGraphFindingGenerator _gen = new();

    [Fact]
    public void Generate_ContestedLocksPresent_EmitsWarningFinding()
    {
        LockGraphDomainResult result = BuildResult(contestedLockCount: 3);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Warning);
    }

    [Fact]
    public void Generate_NoContestedLocks_EmitsInfoFinding()
    {
        LockGraphDomainResult result = BuildResult(contestedLockCount: 0);

        var findings = _gen.Generate(result);

        findings.Should().ContainSingle().Which.Severity.Should().Be(FindingSeverity.Info);
    }

    [Fact]
    public void Generate_TwoOrMoreDeadlockCandidates_RecommendsCycleDetection()
    {
        LockGraphDomainResult result = BuildResult(
            contestedLockCount: 2,
            deadlockCandidateCount: 2,
            deadlockCandidateDetails:
            [
                new DeadlockCandidateSnapshot(ManagedThreadId: 1, OsThreadId: 101, LockObjectTypes: ["System.Object"], LockObjectAddresses: [0x1000], BlockedAtFrame: "Foo.Bar()"),
                new DeadlockCandidateSnapshot(ManagedThreadId: 2, OsThreadId: 102, LockObjectTypes: ["System.Object"], LockObjectAddresses: [0x2000], BlockedAtFrame: "Baz.Qux()"),
            ]);

        var finding = _gen.Generate(result).Single();

        finding.Recommendation.Should().Contain("deadlock pattern");
        finding.Evidence.Should().Contain("thread 1").And.Contain("thread 2");
    }

    [Fact]
    public void Generate_ContestedNoDeadlockCandidates_RecommendsReducingLockScope()
    {
        LockGraphDomainResult result = BuildResult(contestedLockCount: 5, deadlockCandidateCount: 0);

        var finding = _gen.Generate(result).Single();

        finding.Recommendation.Should().Contain("Reduce lock scope");
    }

    [Fact]
    public void Generate_NoContentionAtAll_RecommendsNoAction()
    {
        LockGraphDomainResult result = BuildResult(contestedLockCount: 0, deadlockCandidateCount: 0);

        var finding = _gen.Generate(result).Single();

        finding.Recommendation.Should().Contain("No lock contention detected");
    }

    private static LockGraphDomainResult BuildResult(
        int contestedLockCount,
        int deadlockCandidateCount = 0,
        IReadOnlyList<DeadlockCandidateSnapshot>? deadlockCandidateDetails = null) => new(
        TotalHeldLocks: 20,
        ContestedLockCount: contestedLockCount,
        MaxWaitersOnSingleLock: contestedLockCount > 0 ? 2 : 0,
        DeadlockCandidateCount: deadlockCandidateCount,
        DeadlockCandidateDetails: deadlockCandidateDetails);
}
