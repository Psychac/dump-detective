using System.Reflection;

using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Pipeline;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class HangAnalyzerHeapIndexScanTests
{
    [Fact]
    public void CreateWorkerInstance_ReturnsFreshHangAnalyzer()
    {
        HangAnalyzer primary = new();

        var worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<HangAnalyzer>();
    }

    [Fact]
    public void MergePartial_SumsThreadPoolHeapScanCounters()
    {
        HangAnalyzer primary = new();
        SeedParticipantState(primary,
            queuedWorkItems: 3, totalTasks: 10, pendingTasks: 4, faultedTasks: 1, canceledTasks: 2,
            taskScanLimited: false, tasksScanned: 10, totalContinuations: 5,
            taskContinuations: new());

        HangAnalyzer worker = new();
        SeedParticipantState(worker,
            queuedWorkItems: 2, totalTasks: 8, pendingTasks: 3, faultedTasks: 0, canceledTasks: 1,
            taskScanLimited: false, tasksScanned: 8, totalContinuations: 3,
            taskContinuations: new());

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        var tp = GetThreadPoolInfo(primary);
        tp.QueuedWorkItems.Should().Be(5);
        tp.TotalTasks.Should().Be(18);
        tp.PendingTasks.Should().Be(7);
        tp.FaultedTasks.Should().Be(1);
        tp.CanceledTasks.Should().Be(3);
        tp.TaskScanLimited.Should().BeFalse();
        GetTasksScanned(primary).Should().Be(18);
        GetTotalContinuations(primary).Should().Be(8);
    }

    [Fact]
    public void MergePartial_OrsTaskScanLimited()
    {
        HangAnalyzer primary = new();
        SeedParticipantState(primary, taskScanLimited: false);

        HangAnalyzer worker = new();
        SeedParticipantState(worker, taskScanLimited: true);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetThreadPoolInfo(primary).TaskScanLimited.Should().BeTrue();
    }

    [Fact]
    public void MergePartial_SumsTaskContinuations_PerTypeName()
    {
        HangAnalyzer primary = new();
        SeedParticipantState(primary, taskContinuations: new() { ["TypeA"] = 3, ["TypeB"] = 1 });

        HangAnalyzer worker = new();
        SeedParticipantState(worker, taskContinuations: new() { ["TypeA"] = 2, ["TypeC"] = 4 });

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        GetTaskContinuations(primary).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["TypeA"] = 5,
            ["TypeB"] = 1,
            ["TypeC"] = 4
        });
    }

    [Fact]
    public void MergePartial_UnionsProfileByMethodTableCache_NewKeyAddedExistingPreserved()
    {
        // AsyncTypeProfile is private; seed the cache using reflection and verify key presence/absence.
        HangAnalyzer primary = new();
        var primaryCache = CreateProfileCache();
        primaryCache[0x1000UL] = CreateProfileNone();
        SeedParticipantState(primary, profileByMethodTable: primaryCache);

        HangAnalyzer worker = new();
        var workerCache = CreateProfileCache();
        workerCache[0x1000UL] = CreateProfileNone();
        workerCache[0x2000UL] = CreateProfileNone();
        SeedParticipantState(worker, profileByMethodTable: workerCache);

        ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);

        // Both keys from primary+worker should now be present.
        GetProfileByMethodTableKeys(primary).Should().BeEquivalentTo(new ulong[] { 0x1000UL, 0x2000UL });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    // AsyncTypeProfile is private-nested inside HangAnalyzer; use reflection to create
    // an unboxed instance for seeding the profile cache under test.
    private static System.Collections.IDictionary CreateProfileCache()
    {
        Type profileType = typeof(HangAnalyzer)
            .GetNestedType("AsyncTypeProfile", BindingFlags.NonPublic)!;
        Type dictType = typeof(Dictionary<,>).MakeGenericType(typeof(ulong), profileType);
        return (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
    }

    private static object CreateProfileNone()
    {
        Type profileType = typeof(HangAnalyzer)
            .GetNestedType("AsyncTypeProfile", BindingFlags.NonPublic)!;
        // AsyncTypeProfile is a record struct — default(T) == None
        return Activator.CreateInstance(profileType)!;
    }

    private static void SeedParticipantState(
        HangAnalyzer analyzer,
        int queuedWorkItems = 0,
        int totalTasks = 0,
        int pendingTasks = 0,
        int faultedTasks = 0,
        int canceledTasks = 0,
        bool taskScanLimited = false,
        int tasksScanned = 0,
        int totalContinuations = 0,
        Dictionary<string, int>? taskContinuations = null,
        System.Collections.IDictionary? profileByMethodTable = null)
    {
        Type t = typeof(HangAnalyzer);

        var tp = new ThreadPoolAnalysis
        {
            QueuedWorkItems = queuedWorkItems,
            TotalTasks = totalTasks,
            PendingTasks = pendingTasks,
            FaultedTasks = faultedTasks,
            CanceledTasks = canceledTasks,
            TaskScanLimited = taskScanLimited
        };
        SetField(t, analyzer, "_threadPoolInfo", tp);
        SetField(t, analyzer, "_tasksScanned", tasksScanned);
        SetField(t, analyzer, "_totalContinuations", totalContinuations);
        SetField(t, analyzer, "_taskContinuations", taskContinuations ?? new Dictionary<string, int>());

        if (profileByMethodTable != null)
            SetField(t, analyzer, "_profileByMethodTable", profileByMethodTable);
        else
            SetField(t, analyzer, "_profileByMethodTable", CreateProfileCache());
    }

    private static void SetField(Type type, object instance, string name, object? value) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

    private static T GetField<T>(HangAnalyzer analyzer, string name) =>
        (T)typeof(HangAnalyzer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(analyzer)!;

    private static ThreadPoolAnalysis GetThreadPoolInfo(HangAnalyzer a) => GetField<ThreadPoolAnalysis>(a, "_threadPoolInfo");
    private static int GetTasksScanned(HangAnalyzer a) => GetField<int>(a, "_tasksScanned");
    private static int GetTotalContinuations(HangAnalyzer a) => GetField<int>(a, "_totalContinuations");
    private static Dictionary<string, int> GetTaskContinuations(HangAnalyzer a) => GetField<Dictionary<string, int>>(a, "_taskContinuations");

    private static ICollection<ulong> GetProfileByMethodTableKeys(HangAnalyzer a)
    {
        var dict = (System.Collections.IDictionary)typeof(HangAnalyzer)
            .GetField("_profileByMethodTable", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(a)!;
        return dict.Keys.Cast<ulong>().ToList();
    }
}
