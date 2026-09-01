using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashAnalyzerRethrowTests
{
    private readonly CrashAnalyzer _analyzer = new();

    [Fact]
    public void BuildCrashThreadSnapshots_ExactMatchNotRethrown_KeepsExactConfidence()
    {
        var candidate = new CrashThreadCandidate
        {
            ThreadId = 1,
            PrimaryExceptionType = "FooException",
            OriginalExceptionStack = ["frame1"],
            OriginalExceptionStackIsRethrown = false,
        };
        var analysis = new ExceptionAnalysis { CrashThreadCandidates = [candidate] };

        var snapshots = _analyzer.BuildCrashThreadSnapshotsImpl(analysis);

        snapshots.Should().ContainSingle();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.Exact);
        snapshots[0].OriginalStackTraceIsRethrown.Should().BeFalse();
    }

    [Fact]
    public void BuildCrashThreadSnapshots_ExactMatchRethrown_DowngradesToThreadIdTier()
    {
        var candidate = new CrashThreadCandidate
        {
            ThreadId = 1,
            PrimaryExceptionType = "FooException",
            OriginalExceptionStack = ["frame1"],
            OriginalExceptionStackIsRethrown = true,
        };
        var analysis = new ExceptionAnalysis { CrashThreadCandidates = [candidate] };

        var snapshots = _analyzer.BuildCrashThreadSnapshotsImpl(analysis);

        snapshots.Should().ContainSingle();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.ThreadId);
        snapshots[0].OriginalStackTraceIsRethrown.Should().BeTrue();
    }

    [Fact]
    public void BuildCrashThreadSnapshots_ThreadIdMatchOnRethrownInstance_DowngradesToMessageHResultTier()
    {
        var candidate = new CrashThreadCandidate { ThreadId = 7, PrimaryExceptionType = "FooException" };
        var rethrownInstance = new ExceptionInstance
        {
            Address = 0x2000,
            Type = "FooException",
            ThreadId = 7,
            OriginalStackTrace = ["frame1"],
            IsRethrown = true,
        };
        var analysis = new ExceptionAnalysis
        {
            CrashThreadCandidates = [candidate],
            ExceptionsByType = new() { ["FooException"] = [rethrownInstance] }
        };

        var snapshots = _analyzer.BuildCrashThreadSnapshotsImpl(analysis);

        snapshots.Should().ContainSingle();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.MessageHResult);
        snapshots[0].OriginalStackTraceIsRethrown.Should().BeTrue();
    }

    [Fact]
    public void BuildCrashThreadSnapshots_TypeInnerTypeTierRethrown_StaysAtLowestNonNoneTier()
    {
        var candidate = new CrashThreadCandidate { ThreadId = 9, PrimaryExceptionType = "FooException" };
        var rethrownInstance = new ExceptionInstance
        {
            Address = 0x3000,
            Type = "FooException",
            OriginalStackTrace = ["frame1"],
            IsRethrown = true,
        };
        var analysis = new ExceptionAnalysis
        {
            CrashThreadCandidates = [candidate],
            ExceptionsByType = new() { ["FooException"] = [rethrownInstance] }
        };

        var snapshots = _analyzer.BuildCrashThreadSnapshotsImpl(analysis);

        snapshots.Should().ContainSingle();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.TypeInnerType);
        snapshots[0].OriginalStackTraceIsRethrown.Should().BeTrue();
    }

    [Fact]
    public void BuildCrashThreadSnapshots_NoMatch_StaysNoneRegardlessOfRethrow()
    {
        var candidate = new CrashThreadCandidate { ThreadId = 1, PrimaryExceptionType = "FooException" };
        var analysis = new ExceptionAnalysis { CrashThreadCandidates = [candidate] };

        var snapshots = _analyzer.BuildCrashThreadSnapshotsImpl(analysis);

        snapshots.Should().ContainSingle();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.None);
        snapshots[0].OriginalStackTraceIsRethrown.Should().BeFalse();
        // No CurrentThreadStack frames to resolve a module from — module resolution itself
        // needs a real ClrStackFrame (Method.Type.Module), not unit-testable without a live dump.
        snapshots[0].TopUserFrameModule.Should().BeNull();
    }
}
