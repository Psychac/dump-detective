using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Core.Options;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshCrashAnalyzerWithSameOptions()
    {
        var options = new CrashAnalysisOptions { MaxExceptionsPerType = 3 };
        CrashAnalyzer primary = new(options);

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<CrashAnalyzer>();
        GetOptions((CrashAnalyzer)worker).MaxExceptionsPerType.Should().Be(3);
    }

    [Fact]
    public void MergePartial_SumsTotalsAndTypeCounts()
    {
        CrashAnalyzer primary = SeedAnalyzer(
            maxExceptionsPerType: 10,
            total: 4, active: 1,
            typeCounts: new() { ["FooException"] = 3, ["BarException"] = 1 },
            activeTypeCounts: new() { ["FooException"] = 1 },
            exceptionsByType: new() { ["FooException"] = [], ["BarException"] = [] },
            candidates: new());

        CrashAnalyzer worker = SeedAnalyzer(
            maxExceptionsPerType: 10,
            total: 5, active: 2,
            typeCounts: new() { ["FooException"] = 2, ["BazException"] = 3 },
            activeTypeCounts: new() { ["FooException"] = 1, ["BazException"] = 1 },
            exceptionsByType: new() { ["FooException"] = [], ["BazException"] = [] },
            candidates: new());

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetTotal(primary).Should().Be(9);
        GetActive(primary).Should().Be(3);
        GetTypeCounts(primary).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FooException"] = 5,
            ["BarException"] = 1,
            ["BazException"] = 3
        });
        GetActiveTypeCounts(primary).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FooException"] = 2,
            ["BazException"] = 1
        });
    }

    [Fact]
    public void MergePartial_KeepsActiveInstancesUnconditionally_AndCapsNonActiveAcrossWorkers()
    {
        // self (lowest address range) already holds its own locally-capped list; worker holds
        // the next-higher address range's locally-capped list. Cap is 2 non-active per type.
        var selfList = new List<ExceptionInstance>
        {
            NonActive(0x1000),
            NonActive(0x1100)
        };
        var workerList = new List<ExceptionInstance>
        {
            NonActive(0x2000),
            Active(0x2100, threadId: 7)
        };

        CrashAnalyzer primary = SeedAnalyzer(
            maxExceptionsPerType: 2,
            total: 2, active: 0,
            typeCounts: new() { ["FooException"] = 2 },
            activeTypeCounts: new(),
            exceptionsByType: new() { ["FooException"] = selfList },
            candidates: new());

        CrashAnalyzer worker = SeedAnalyzer(
            maxExceptionsPerType: 2,
            total: 2, active: 1,
            typeCounts: new() { ["FooException"] = 2 },
            activeTypeCounts: new() { ["FooException"] = 1 },
            exceptionsByType: new() { ["FooException"] = workerList },
            candidates: new());

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        List<ExceptionInstance> merged = GetExceptionsByType(primary)["FooException"];
        merged.Select(i => i.Address).Should().Equal(0x1000UL, 0x1100UL, 0x2100UL);
    }

    [Fact]
    public void MergePartial_SumsActiveExceptionCount_ForSameThreadCandidateAcrossWorkers()
    {
        var selfCandidate = new CrashThreadCandidate
        {
            ThreadId = 42,
            ActiveExceptionCount = 1,
            PrimaryExceptionType = "FooException"
        };
        var workerCandidate = new CrashThreadCandidate
        {
            ThreadId = 42,
            ActiveExceptionCount = 2,
            PrimaryExceptionType = "FooException",
            SampleMessage = "boom"
        };

        CrashAnalyzer primary = SeedAnalyzer(
            maxExceptionsPerType: 10, total: 1, active: 1,
            typeCounts: new(), activeTypeCounts: new(),
            exceptionsByType: new(),
            candidates: new() { [42u] = selfCandidate });

        CrashAnalyzer worker = SeedAnalyzer(
            maxExceptionsPerType: 10, total: 2, active: 2,
            typeCounts: new(), activeTypeCounts: new(),
            exceptionsByType: new(),
            candidates: new() { [42u] = workerCandidate });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var mergedCandidates = GetCandidates(primary);
        mergedCandidates.Should().ContainKey(42u);
        mergedCandidates[42u].ActiveExceptionCount.Should().Be(3);
        mergedCandidates[42u].SampleMessage.Should().Be("boom");
    }

    [Fact]
    public void MergePartial_SumsGenerationAndAggregateInnerExceptionCountsAcrossWorkers()
    {
        CrashAnalyzer primary = SeedAnalyzer(
            maxExceptionsPerType: 10, total: 1, active: 0,
            typeCounts: new(), activeTypeCounts: new(),
            exceptionsByType: new(), candidates: new());
        SetField(typeof(CrashAnalyzer), primary, "_exceptionGen0Counts", new Dictionary<string, int> { ["FooException"] = 2 });
        SetField(typeof(CrashAnalyzer), primary, "_aggregateExceptionCount", 1);
        SetField(typeof(CrashAnalyzer), primary, "_aggregateInnerExceptionTypeCounts", new Dictionary<string, int> { ["System.IO.IOException"] = 1 });
        SetField(typeof(CrashAnalyzer), primary, "_exceptionHeapSizeByType", new Dictionary<string, ulong> { ["FooException"] = 100UL });

        CrashAnalyzer worker = SeedAnalyzer(
            maxExceptionsPerType: 10, total: 1, active: 0,
            typeCounts: new(), activeTypeCounts: new(),
            exceptionsByType: new(), candidates: new());
        SetField(typeof(CrashAnalyzer), worker, "_exceptionGen0Counts", new Dictionary<string, int> { ["FooException"] = 3, ["BarException"] = 1 });
        SetField(typeof(CrashAnalyzer), worker, "_aggregateExceptionCount", 2);
        SetField(typeof(CrashAnalyzer), worker, "_aggregateInnerExceptionTypeCounts", new Dictionary<string, int> { ["System.IO.IOException"] = 2, ["System.TimeoutException"] = 1 });
        SetField(typeof(CrashAnalyzer), worker, "_exceptionHeapSizeByType", new Dictionary<string, ulong> { ["FooException"] = 50UL, ["BarException"] = 25UL });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetGen0Counts(primary).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["FooException"] = 5,
            ["BarException"] = 1
        });
        GetAggregateExceptionCount(primary).Should().Be(3);
        GetAggregateInnerExceptionTypeCounts(primary).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["System.IO.IOException"] = 3,
            ["System.TimeoutException"] = 1
        });
        GetExceptionHeapSizeByType(primary).Should().BeEquivalentTo(new Dictionary<string, ulong>
        {
            ["FooException"] = 150UL,
            ["BarException"] = 25UL
        });
    }

    private static ExceptionInstance NonActive(ulong address) => new() { Address = address };

    private static ExceptionInstance Active(ulong address, uint threadId) => new()
    {
        Address = address,
        ThreadId = threadId
    };

    private static CrashAnalyzer SeedAnalyzer(
        int maxExceptionsPerType,
        int total,
        int active,
        Dictionary<string, int> typeCounts,
        Dictionary<string, int> activeTypeCounts,
        Dictionary<string, List<ExceptionInstance>> exceptionsByType,
        Dictionary<uint, CrashThreadCandidate> candidates)
    {
        var options = new CrashAnalysisOptions { MaxExceptionsPerType = maxExceptionsPerType };
        CrashAnalyzer analyzer = new(options);

        Type type = typeof(CrashAnalyzer);
        SetField(type, analyzer, "_totalExceptions", total);
        SetField(type, analyzer, "_activeExceptionsCount", active);
        SetField(type, analyzer, "_exceptionTypeCounts", typeCounts);
        SetField(type, analyzer, "_activeExceptionTypeCounts", activeTypeCounts);
        SetField(type, analyzer, "_exceptionsByType", exceptionsByType);
        SetField(type, analyzer, "_crashThreadCandidates", candidates);
        SetField(type, analyzer, "_exceptionGen0Counts", new Dictionary<string, int>());
        SetField(type, analyzer, "_exceptionGen1Counts", new Dictionary<string, int>());
        SetField(type, analyzer, "_exceptionGen2Counts", new Dictionary<string, int>());
        SetField(type, analyzer, "_exceptionLohCounts", new Dictionary<string, int>());
        SetField(type, analyzer, "_aggregateExceptionCount", 0);
        SetField(type, analyzer, "_aggregateInnerExceptionTypeCounts", new Dictionary<string, int>());
        SetField(type, analyzer, "_exceptionHeapSizeByType", new Dictionary<string, ulong>());

        return analyzer;
    }

    private static void SetField(Type type, object instance, string fieldName, object? value) =>
        type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static CrashAnalysisOptions GetOptions(CrashAnalyzer analyzer) =>
        (CrashAnalysisOptions)typeof(CrashAnalyzer)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static int GetTotal(CrashAnalyzer analyzer) =>
        (int)typeof(CrashAnalyzer)
            .GetField("_totalExceptions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static int GetActive(CrashAnalyzer analyzer) =>
        (int)typeof(CrashAnalyzer)
            .GetField("_activeExceptionsCount", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, int> GetTypeCounts(CrashAnalyzer analyzer) =>
        (Dictionary<string, int>)typeof(CrashAnalyzer)
            .GetField("_exceptionTypeCounts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, int> GetActiveTypeCounts(CrashAnalyzer analyzer) =>
        (Dictionary<string, int>)typeof(CrashAnalyzer)
            .GetField("_activeExceptionTypeCounts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, List<ExceptionInstance>> GetExceptionsByType(CrashAnalyzer analyzer) =>
        (Dictionary<string, List<ExceptionInstance>>)typeof(CrashAnalyzer)
            .GetField("_exceptionsByType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<uint, CrashThreadCandidate> GetCandidates(CrashAnalyzer analyzer) =>
        (Dictionary<uint, CrashThreadCandidate>)typeof(CrashAnalyzer)
            .GetField("_crashThreadCandidates", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, int> GetGen0Counts(CrashAnalyzer analyzer) =>
        (Dictionary<string, int>)typeof(CrashAnalyzer)
            .GetField("_exceptionGen0Counts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static int GetAggregateExceptionCount(CrashAnalyzer analyzer) =>
        (int)typeof(CrashAnalyzer)
            .GetField("_aggregateExceptionCount", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, int> GetAggregateInnerExceptionTypeCounts(CrashAnalyzer analyzer) =>
        (Dictionary<string, int>)typeof(CrashAnalyzer)
            .GetField("_aggregateInnerExceptionTypeCounts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;

    private static Dictionary<string, ulong> GetExceptionHeapSizeByType(CrashAnalyzer analyzer) =>
        (Dictionary<string, ulong>)typeof(CrashAnalyzer)
            .GetField("_exceptionHeapSizeByType", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(analyzer)!;
}
