using DumpDetective.Analysis.Models;
using DumpDetective.Reporting.Models;
using DumpDetective.Reporting.SectionBuilders;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Reporting;

public sealed class ExceptionAnalysisSectionBuilderTests
{
    private readonly ExceptionAnalysisSectionBuilder _builder = new();

    [Fact]
    public void Build_AllCandidatesExact_YieldsHighConfidenceLeadFinding()
    {
        var crash = CrashResult(activeExceptions: 3, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, InferenceConfidence.Exact)
        ]);

        var section = _builder.Build(crash);

        section.LeadFinding.Should().NotBeNull();
        section.LeadFinding!.ConfidenceScore.Should().BeApproximately(0.95, 0.0001);
        section.LeadFinding.ConfidenceSymbol.Should().Be("●●●●");
    }

    [Fact]
    public void Build_AllCandidatesNone_YieldsLowConfidenceLeadFinding()
    {
        var crash = CrashResult(activeExceptions: 2, candidates:
        [
            Candidate(threadId: 1, activeCount: 2, InferenceConfidence.None)
        ]);

        var section = _builder.Build(crash);

        section.LeadFinding!.ConfidenceScore.Should().BeApproximately(0.15, 0.0001);
        section.LeadFinding.ConfidenceSymbol.Should().Be("●○○○");
    }

    [Fact]
    public void Build_MixedConfidenceTiers_WeightsByActiveExceptionCount()
    {
        var crash = CrashResult(activeExceptions: 4, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, InferenceConfidence.Exact),
            Candidate(threadId: 2, activeCount: 1, InferenceConfidence.None),
        ]);

        var section = _builder.Build(crash);

        // (0.95*3 + 0.15*1) / 4 = 0.75
        section.LeadFinding!.ConfidenceScore.Should().BeApproximately(0.75, 0.0001);
        section.LeadFinding.ConfidenceSymbol.Should().Be("●●●○");
        section.LeadFinding.Caveats.Should().Contain(c => c.Contains("Exact") && c.Contains("None"));
    }

    [Fact]
    public void Build_NoActiveExceptions_OmitsLeadFinding()
    {
        var crash = CrashResult(activeExceptions: 0, candidates: []);

        var section = _builder.Build(crash);

        section.LeadFinding.Should().BeNull();
    }

    [Fact]
    public void Build_ExceptionHeapSizeByType_AddsSizeTableAndKeyMetric()
    {
        var crash = CrashResult(activeExceptions: 0, candidates: []) with
        {
            ExceptionHeapSizeByType = new Dictionary<string, ulong>
            {
                ["FooException"] = 2048UL,
                ["BarException"] = 512UL,
            }
        };

        var section = _builder.Build(crash);

        section.CompactTables.Should().NotBeNull();
        var sizeTable = section.CompactTables!.Should().ContainSingle(t => t.Title == "Exception heap size by type").Subject;
        sizeTable.Rows.Should().HaveCount(2);
        sizeTable.Rows[0].Values[0].Should().Be("FooException");

        section.KeyMetrics.Should().ContainKey("exception_heap_bytes");
        section.KeyMetrics!["exception_heap_bytes"].Should().BeOfType<NumericMetricValue>()
            .Which.Value.Should().Be(2560d);
    }

    [Fact]
    public void Build_CandidatesWithTopUserFrameModule_AddsAssemblyAttributionTable()
    {
        var crash = CrashResult(activeExceptions: 4, candidates:
        [
            Candidate(threadId: 1, activeCount: 3, InferenceConfidence.Exact, topUserFrameModule: "MyApp.DataLayer.dll"),
            Candidate(threadId: 2, activeCount: 1, InferenceConfidence.Exact, topUserFrameModule: "MyApp.Web.dll"),
        ]);

        var section = _builder.Build(crash);

        section.CompactTables.Should().NotBeNull();
        var attributionTable = section.CompactTables!.Should()
            .ContainSingle(t => t.Title == "Exception attribution by assembly (active crash threads)").Subject;
        attributionTable.Rows.Should().HaveCount(2);
        attributionTable.Rows[0].Values[0].Should().Be("MyApp.DataLayer.dll");
        attributionTable.Rows[0].Values[1].Should().Be(3);
        attributionTable.Rows[0].Values[2].Should().Be(75.0);
        attributionTable.Rows[1].Values[0].Should().Be("MyApp.Web.dll");
        attributionTable.Rows[1].Values[2].Should().Be(25.0);
    }

    [Fact]
    public void Build_CandidatesWithoutTopUserFrameModule_OmitsAssemblyAttributionTable()
    {
        var crash = CrashResult(activeExceptions: 1, candidates:
        [
            Candidate(threadId: 1, activeCount: 1, InferenceConfidence.None)
        ]);

        var section = _builder.Build(crash);

        section.CompactTables.Should().NotBeNull();
        section.CompactTables!.Should().NotContain(t => t.Title == "Exception attribution by assembly (active crash threads)");
    }

    [Fact]
    public void Build_Gen2RetentionPaths_AddsRetentionPathTable()
    {
        var crash = CrashResult(activeExceptions: 0, candidates: []) with
        {
            Gen2RetentionPaths =
            [
                new ExceptionRetentionPath("FooException", 0x1000, "Static", "Static: MyApp.Cache.Instance -> FooException", SearchTruncated: false)
            ]
        };

        var section = _builder.Build(crash);

        section.CompactTables.Should().NotBeNull();
        var retentionTable = section.CompactTables!.Should()
            .ContainSingle(t => t.Title == "Gen2/LOH exception retention paths").Subject;
        retentionTable.Rows.Should().ContainSingle();
        retentionTable.Rows[0].Values[0].Should().Be("FooException");
        retentionTable.Rows[0].Values[2].Should().Be("Static");
        retentionTable.Rows[0].Values[4].Should().Be("No");
    }

    [Fact]
    public void Build_NoGen2RetentionPaths_OmitsRetentionPathTable()
    {
        var crash = CrashResult(activeExceptions: 0, candidates: []);

        var section = _builder.Build(crash);

        section.CompactTables.Should().NotBeNull();
        section.CompactTables!.Should().NotContain(t => t.Title == "Gen2/LOH exception retention paths");
    }

    private static CrashDomainResult CrashResult(int activeExceptions, IReadOnlyList<CrashThreadCandidateSnapshot> candidates) => new(
        TotalExceptions: activeExceptions + 10,
        ActiveExceptions: activeExceptions,
        ExceptionTypeCounts: new Dictionary<string, int> { ["FooException"] = activeExceptions + 10 },
        ActiveExceptionTypeCounts: new Dictionary<string, int> { ["FooException"] = activeExceptions },
        TopCrashThreadCandidates: candidates);

    private static CrashThreadCandidateSnapshot Candidate(uint threadId, int activeCount, InferenceConfidence confidence, string? topUserFrameModule = null) => new(
        ThreadId: threadId,
        OSThreadId: threadId,
        ActiveExceptionCount: activeCount,
        PrimaryExceptionType: "FooException",
        TopFrames: [],
        OriginalStackTrace: null,
        OriginalStackTraceInferred: confidence != InferenceConfidence.Exact,
        OriginalStackTraceInferredFrom: null,
        OriginalStackTraceConfidence: confidence,
        TopUserFrameModule: topUserFrameModule);
}
