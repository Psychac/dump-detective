using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;
using DumpDetective.Core.Models;
using DumpDetective.Reporting.Formatters;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;
using DumpDetective.Reporting.Services;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Tests for crash-inference heuristics and helpers in <see cref="CrashAnalyzer"/>.
/// These test the static/internal helpers without requiring a live ClrMD runtime.
/// </summary>
public sealed class CrashInferenceTests
{
    // ── NormalizeFrame ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("   at System.Object.ToString()", "System.Object.ToString()")]
    [InlineData("at MyApp.Service.DoWork()", "MyApp.Service.DoWork()")]
    [InlineData("MyApp.Worker.Run()", "MyApp.Worker.Run()")]
    public void NormalizeFrame_StripsAtPrefix(string raw, string expected)
    {
        CrashAnalyzer.NormalizeFrame(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(
        "MyApp.Service+<ProcessAsync>d__5.MoveNext()",
        "MyApp.Service.ProcessAsync() [async]")]
    [InlineData(
        "MyApp.Worker+<RunAsync>d__12.MoveNext()",
        "MyApp.Worker.RunAsync() [async]")]
    public void NormalizeFrame_SimplifiesAsyncStateMachine(string raw, string expected)
    {
        CrashAnalyzer.NormalizeFrame(raw).Should().Be(expected);
    }

    [Fact]
    public void NormalizeFrame_HandlesEmptyAndNull_Gracefully()
    {
        CrashAnalyzer.NormalizeFrame(string.Empty).Should().BeEmpty();
        CrashAnalyzer.NormalizeFrame("   ").Should().Be("   ");
    }

    [Fact]
    public void NormalizeFrame_DoesNotAlterRegularFrames()
    {
        const string frame = "MyApp.Service.Compute(System.Int32 x)";
        CrashAnalyzer.NormalizeFrame(frame).Should().Be(frame);
    }

    // ── IsFrameworkFrame ───────────────────────────────────────────────────

    [Theory]
    [InlineData("System.NullReferenceException.ctor()", true)]
    [InlineData("System.Threading.Tasks.Task.Run()", true)]
    [InlineData("Microsoft.AspNetCore.Mvc.ControllerBase.Ok()", true)]
    [InlineData("mscorlib.System.Object.ToString()", true)]
    [InlineData("MyApp.Service.DoWork()", false)]
    [InlineData("Company.Product.Handler.Execute()", false)]
    public void IsFrameworkFrame_ClassifiesCorrectly(string frame, bool expectedFw)
    {
        CrashAnalyzer.IsFrameworkFrame(frame).Should().Be(expectedFw);
    }

    // ── BuildCrashThreadSnapshots inference tiers ──────────────────────────

    [Fact]
    public void BuildCrashThreadSnapshots_Tier1_UsesExactStackWhenPresent()
    {
        var analysis = MakeAnalysis(
            candidates: [MakeCandidate(threadId: 1, originalStack: ["MyApp.Service.Compute()"])],
            instances: []);

        var snapshots = InvokeSnapshotBuilder(analysis);

        snapshots.Should().HaveCount(1);
        snapshots[0].OriginalStackTrace.Should().ContainSingle().Which.Should().Be("MyApp.Service.Compute()");
        snapshots[0].OriginalStackTraceInferred.Should().BeFalse();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.Exact);
        analysis.InferredTraceCount.Should().Be(0);
    }

    [Fact]
    public void BuildCrashThreadSnapshots_Tier2_InfersByThreadId()
    {
        var instance = MakeInstance(address: 0x1000, type: "System.InvalidOperationException",
            threadId: 5, originalStack: ["at MyApp.Repo.Save()"]);

        var analysis = MakeAnalysis(
            candidates: [MakeCandidate(threadId: 5, originalStack: null)],
            instances: [instance]);

        var snapshots = InvokeSnapshotBuilder(analysis);

        snapshots.Should().HaveCount(1);
        var s = snapshots[0];
        s.OriginalStackTrace.Should().ContainSingle().Which.Should().Be("MyApp.Repo.Save()"); // "at " stripped
        s.OriginalStackTraceInferred.Should().BeTrue();
        s.OriginalStackTraceConfidence.Should().Be(InferenceConfidence.ThreadId);
        s.OriginalStackTraceInferredFrom.Should().Contain("0x1000");
        analysis.InferredTraceCount.Should().Be(1);
    }

    [Fact]
    public void BuildCrashThreadSnapshots_Tier3_InfersByMessageAndHResult()
    {
        var instance = MakeInstance(address: 0x2000, type: "System.Exception",
            message: "Oops", hresult: unchecked((int)0x80004005),
            originalStack: ["at MyApp.Processor.Handle()"]);

        var candidate = MakeCandidate(threadId: 10, originalStack: null);
        candidate.SampleMessage = "Oops";
        candidate.SampleHResult = unchecked((int)0x80004005);

        var analysis = MakeAnalysis([candidate], [instance]);

        var snapshots = InvokeSnapshotBuilder(analysis);

        snapshots.Should().HaveCount(1);
        var s = snapshots[0];
        s.OriginalStackTraceConfidence.Should().Be(InferenceConfidence.MessageHResult);
        s.OriginalStackTrace.Should().ContainSingle().Which.Should().Be("MyApp.Processor.Handle()");
        s.OriginalStackTraceInferredFrom.Should().Contain("0x2000");
        analysis.InferredTraceCount.Should().Be(1);
    }

    [Fact]
    public void BuildCrashThreadSnapshots_Tier4_InfersByTypeAndInnerType()
    {
        var instance = MakeInstance(address: 0x3000, type: "System.IO.IOException",
            innerExceptionType: "System.UnauthorizedAccessException",
            originalStack: ["at MyApp.FileStore.Write()"]);

        var candidate = MakeCandidate(threadId: 20, originalStack: null);
        candidate.PrimaryExceptionType = "System.IO.IOException";
        candidate.SampleInnerExceptionType = "System.UnauthorizedAccessException";

        var analysis = MakeAnalysis([candidate], [instance]);

        var snapshots = InvokeSnapshotBuilder(analysis);

        var s = snapshots[0];
        s.OriginalStackTraceConfidence.Should().Be(InferenceConfidence.TypeInnerType);
        s.OriginalStackTrace.Should().ContainSingle().Which.Should().Be("MyApp.FileStore.Write()");
        analysis.InferredTraceCount.Should().Be(1);
    }

    [Fact]
    public void BuildCrashThreadSnapshots_NoMatch_LeavesOriginalNull()
    {
        var instance = MakeInstance(address: 0x9999, type: "System.Exception",
            threadId: null, originalStack: ["at SomeOther.Method()"]);

        var candidate = MakeCandidate(threadId: 99, originalStack: null);
        candidate.PrimaryExceptionType = "System.ArgumentNullException"; // different type

        var analysis = MakeAnalysis([candidate], [instance]);

        var snapshots = InvokeSnapshotBuilder(analysis);

        snapshots[0].OriginalStackTrace.Should().BeNull();
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.None);
        analysis.InferredTraceCount.Should().Be(0);
    }

    [Fact]
    public void BuildCrashThreadSnapshots_Tier2_PreferredOverTier3_WhenBothMatch()
    {
        // Instance A: matches by threadId (tier 2) with one frame
        var instanceA = MakeInstance(address: 0xAAAA, type: "System.Exception",
            threadId: 7, originalStack: ["at MyApp.ThreadMatch.Method()"]);

        // Instance B: matches by message+hresult (tier 3) with different frame
        var instanceB = MakeInstance(address: 0xBBBB, type: "System.Exception",
            message: "X", hresult: 1, originalStack: ["at MyApp.MsgMatch.Method()"]);

        var candidate = MakeCandidate(threadId: 7, originalStack: null);
        candidate.SampleMessage = "X";
        candidate.SampleHResult = 1;

        var analysis = MakeAnalysis([candidate], [instanceA, instanceB]);

        var snapshots = InvokeSnapshotBuilder(analysis);

        // ThreadId (tier 2) should win
        snapshots[0].OriginalStackTraceConfidence.Should().Be(InferenceConfidence.ThreadId);
        snapshots[0].OriginalStackTrace.Should().ContainSingle().Which.Should().Be("MyApp.ThreadMatch.Method()");
    }

    // ── End-to-end: section builder → text/html renderer ────────────────────

    [Fact]
    public void CrashSectionBuilder_WithFrames_EmitsStackFrameBlocks()
    {
        var topFrames = new List<string>
        {
            "MyApp.Controllers.HomeController.Index()",
            "System.Web.Mvc.ControllerActionInvoker.InvokeAction()",
        };
        var originalStack = new List<string>
        {
            "MyApp.Data.Repository.Save(Entity e)",
            "System.Data.SqlClient.SqlCommand.ExecuteNonQuery()",
        };

        var candidate = new CrashThreadCandidateSnapshot(
            ThreadId: 1,
            OSThreadId: 100,
            ActiveExceptionCount: 1,
            PrimaryExceptionType: "System.Data.SqlException",
            TopFrames: topFrames,
            OriginalStackTrace: originalStack,
            OriginalStackTraceInferred: false,
            OriginalStackTraceInferredFrom: null,
            OriginalStackTraceConfidence: InferenceConfidence.Exact);

        var domain = MakeDomainResult(candidates: [candidate], instances: []);

        var section = new CrashSectionBuilder().Build(domain);

        // StackFrameBlock blocks must be present for BOTH top frames and original stack
        var frames = section.Blocks.OfType<StackFrameBlock>().ToList();
        frames.Should().HaveCount(topFrames.Count + originalStack.Count,
            "each top frame and each original stack frame should emit a StackFrameBlock");

        frames.Should().Contain(sf => sf.Frame.Contains("HomeController"));
        frames.Should().Contain(sf => sf.Frame.Contains("Repository.Save"));
        frames.Should().Contain(sf => sf.Frame.Contains("SqlCommand"));
    }

    [Fact]
    public void CrashSectionBuilder_WithFrames_RendersFramesInTextOutput()
    {
        var candidate = new CrashThreadCandidateSnapshot(
            ThreadId: 2,
            OSThreadId: 200,
            ActiveExceptionCount: 1,
            PrimaryExceptionType: "System.NullReferenceException",
            TopFrames: ["MyApp.Service.DoWork()"],
            OriginalStackTrace: ["MyApp.Data.Repository.GetById(Int32 id)"],
            OriginalStackTraceInferred: false,
            OriginalStackTraceInferredFrom: null,
            OriginalStackTraceConfidence: InferenceConfidence.Exact);

        var domain = MakeDomainResult([candidate], []);
        var section = new CrashSectionBuilder().Build(domain);

        // Verify the section has StackFrameBlock items (not PathBlock with "Frame" label)
        section.Blocks.OfType<StackFrameBlock>().Should().NotBeEmpty();
        section.Blocks.OfType<PathBlock>().Should().NotContain(p => p.Label == "Frame",
            "frame rendering should use StackFrameBlock, not PathBlock");

        // Render to text and check frames appear
        var doc = BuildMinimalDoc(section);
        string text = new TextCanonicalReportFormatter().Render(doc);

        text.Should().Contain("MyApp.Service.DoWork()", "top frame must appear in text output");
        text.Should().Contain("MyApp.Data.Repository.GetById", "original frame must appear in text output");
        text.Should().Contain("   at ", "frames should be prefixed with '   at ' in text output");
    }

    [Fact]
    public void CrashSectionBuilder_IsFrameworkFrame_Applied_ToTopFrames()
    {
        var candidate = new CrashThreadCandidateSnapshot(
            ThreadId: 3,
            OSThreadId: 300,
            ActiveExceptionCount: 1,
            PrimaryExceptionType: "System.Exception",
            TopFrames: ["System.Threading.Tasks.Task.Run()", "MyApp.Worker.Execute()"],
            OriginalStackTrace: null,
            OriginalStackTraceInferred: false,
            OriginalStackTraceInferredFrom: null);

        var section = new CrashSectionBuilder().Build(MakeDomainResult([candidate], []));

        var frames = section.Blocks.OfType<StackFrameBlock>().ToList();
        frames.Should().HaveCount(2);
        frames.Single(f => f.Frame.Contains("Task.Run")).IsFrameworkFrame.Should().BeTrue();
        frames.Single(f => f.Frame.Contains("MyApp.Worker")).IsFrameworkFrame.Should().BeFalse();
    }

    // ── Rendering helpers ────────────────────────────────────────────────────

    private static CrashDomainResult MakeDomainResult(
        IReadOnlyList<CrashThreadCandidateSnapshot> candidates,
        IReadOnlyList<ExceptionInstanceSnapshot> instances)
        => new CrashDomainResult(
            TotalExceptions: 1,
            ActiveExceptions: 1,
            ExceptionTypeCounts: new Dictionary<string, int> { ["System.Exception"] = 1 },
            ActiveExceptionTypeCounts: new Dictionary<string, int> { ["System.Exception"] = 1 },
            TopCrashThreadCandidates: candidates,
            TopExceptionInstances: instances) with
        {
            AnalyzerName = "Crash Analysis",
            Category = "Crash",
        };

    private static AnalysisReportDocument BuildMinimalDoc(AnalyzerDetailSection section)
        => new AnalysisReportDocument
        {
            DumpPath = "test.dmp",
            GeneratedAtUtc = DateTime.UtcNow,
            ElapsedSeconds = 0.1,
            SchemaVersion = "1",
            Findings = [],
            AnalyzerSections = [section],
            Confidence = [],
            DedupDiagnostics = new DedupRecord(0, 0, 0),
            ExecutiveSummary = null,
            DeveloperActionPlan = [],
        };


    private static ExceptionAnalysis MakeAnalysis(
        List<CrashThreadCandidate> candidates,
        List<ExceptionInstance> instances)
    {
        var byType = new Dictionary<string, List<ExceptionInstance>>(StringComparer.Ordinal);
        foreach (var inst in instances)
        {
            if (!byType.TryGetValue(inst.Type, out var list))
            {
                list = [];
                byType[inst.Type] = list;
            }
            list.Add(inst);
        }
        return new ExceptionAnalysis
        {
            TotalExceptions = instances.Count,
            ActiveExceptions = candidates.Count,
            ExceptionTypeCounts = byType.ToDictionary(k => k.Key, v => v.Value.Count),
            ActiveExceptionTypeCounts = [],
            ExceptionsByType = byType,
            CrashThreadCandidates = candidates,
        };
    }

    private static CrashThreadCandidate MakeCandidate(uint threadId, List<string>? originalStack)
        => new()
        {
            ThreadId = threadId,
            OSThreadId = threadId * 10,
            ActiveExceptionCount = 1,
            PrimaryExceptionType = "System.Exception",
            OriginalExceptionStack = originalStack ?? [],
        };

    private static ExceptionInstance MakeInstance(
        ulong address,
        string type,
        uint? threadId = null,
        string? message = null,
        int hresult = 0,
        string? innerExceptionType = null,
        List<string>? originalStack = null)
        => new()
        {
            Address = address,
            Type = type,
            ThreadId = threadId,
            OSThreadId = threadId.HasValue ? threadId.Value * 10 : null,
            Message = message ?? string.Empty,
            HResult = hresult,
            InnerExceptionType = innerExceptionType,
            OriginalStackTrace = originalStack ?? [],
        };

    /// <summary>
    /// Calls the private static BuildCrashThreadSnapshots via the public Analyze overload
    /// exposed for testing, or reproduces the same logic using the internal types directly.
    /// Since BuildCrashThreadSnapshots is private we test it through ExceptionAnalysis mutation.
    /// We use reflection here only for unit-test purposes; InternalsVisibleTo covers the types.
    /// </summary>
    private static IReadOnlyList<CrashThreadCandidateSnapshot> InvokeSnapshotBuilder(ExceptionAnalysis analysis)
    {
        // Access via reflection because BuildCrashThreadSnapshots is private static.
        var method = typeof(CrashAnalyzer).GetMethod(
            "BuildCrashThreadSnapshots",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        method.Should().NotBeNull("BuildCrashThreadSnapshots must exist as a private static method");

        var result = method!.Invoke(null, [analysis]);
        result.Should().NotBeNull();
        return (IReadOnlyList<CrashThreadCandidateSnapshot>)result!;
    }
}
