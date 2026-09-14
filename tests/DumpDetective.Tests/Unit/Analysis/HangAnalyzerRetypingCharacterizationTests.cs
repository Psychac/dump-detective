using DumpDetective.Analysis.Analyzers.Threads;
using DumpDetective.Analysis.Indexing;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using FluentAssertions;

using Microsoft.Diagnostics.Runtime;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="HangAnalyzer"/>'s old test coverage (<c>HangAnalyzerHeapIndexScanTests</c>) exercised
/// the pre-retyping analyzer's own private accumulator fields directly via reflection -- that state
/// no longer exists post-retyping (moved to <see cref="HangAnalyzer"/> in a much
/// simpler shape: a flat candidate list, not a per-method-table profile cache plus separate
/// counters), so this replaces it rather than adapting it. Fake, dictionary/list-backed capability
/// implementations, same shape established for the rest of the thread-domain quartet.
/// </summary>
public sealed class HangAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_AliveThreadBlockedOnMonitorWait_CountedAndClassified()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, lockCount: 2), Frame("System.Threading.Monitor.Wait"));

        HangDomainResult result = await Analyze(threads: threads);

        result.TotalAliveThreads.Should().Be(1);
        result.ThreadsHoldingLocks.Should().Be(1);
        result.WaitingThreadCount.Should().Be(1);
        result.WaitCategoryBreakdown.Should().ContainKey("MonitorWait").WhoseValue.Should().Be(1);
        result.TopWaitingThreads.Should().ContainSingle().Which.LockCount.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_DeadThread_ExcludedFromAliveCount()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: false));

        HangDomainResult result = await Analyze(threads: threads);

        result.TotalAliveThreads.Should().Be(0);
        result.WaitingThreadCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_NonWaitingFrame_NotClassifiedAsWaiting()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("MyApp.Worker.DoWork"));

        HangDomainResult result = await Analyze(threads: threads);

        result.WaitingThreadCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_TaskCandidate_ReadsStateFlags_ClassifiesFaulted()
    {
        var candidates = new[] { Candidate(0x1000, "System.Threading.Tasks.Task") };
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "m_stateFlags", 0x200000); // faulted bit

        HangDomainResult result = await Analyze(candidates: candidates, fields: fields);

        result.TotalTasks.Should().Be(1);
        result.FaultedTasks.Should().Be(1);
        result.PendingTasks.Should().Be(0);
        result.CanceledTasks.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_TaskCandidate_NotCompletedNotFaultedNotCanceled_CountsPending()
    {
        var candidates = new[] { Candidate(0x1000, "System.Threading.Tasks.Task") };
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "m_stateFlags", 0); // no bits set -- not completed

        HangDomainResult result = await Analyze(candidates: candidates, fields: fields);

        result.PendingTasks.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_TaskCandidate_CompletedSuccessfully_CountsNeitherPendingFaultedNorCanceled()
    {
        var candidates = new[] { Candidate(0x1000, "System.Threading.Tasks.Task") };
        var fields = new FakeDumpObjectFieldQuery();
        fields.SetInt32(0x1000, "m_stateFlags", 0x1000000); // completed bit only

        HangDomainResult result = await Analyze(candidates: candidates, fields: fields);

        result.TotalTasks.Should().Be(1);
        result.PendingTasks.Should().Be(0);
        result.FaultedTasks.Should().Be(0);
        result.CanceledTasks.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_QueuedWorkItemCandidate_CountsQueuedWorkItems()
    {
        var candidates = new[] { Candidate(0x2000, "System.Threading.ThreadPoolWorkQueue+QueueUserWorkItemCallback") };

        HangDomainResult result = await Analyze(candidates: candidates);

        result.QueuedWorkItems.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ContinuationCandidate_TracksTypeCount()
    {
        var candidates = new[] { Candidate(0x3000, "System.Threading.Tasks.AwaitTaskContinuation") };

        HangDomainResult result = await Analyze(candidates: candidates);

        result.TotalTaskContinuations.Should().Be(1);
        result.TopContinuationTypes.Should().ContainSingle(t => t.Name == "System.Threading.Tasks.AwaitTaskContinuation");
    }

    [Fact]
    public async Task AnalyzeAsync_RuntimeThreadPoolAvailable_PopulatesRuntimeFields()
    {
        var threadPool = new FakeRuntimeThreadPoolQuery();
        threadPool.SetInfo(new RuntimeThreadPoolInfo(MinThreads: 4, MaxThreads: 32, ActiveWorkerThreads: 32, IdleWorkerThreads: 0, RetiredWorkerThreads: 0, QueueLength: 50, CpuUtilization: 10));

        HangDomainResult result = await Analyze(threadPool: threadPool);

        result.RuntimeThreadPoolDataAvailable.Should().BeTrue();
        result.RuntimeMinThreads.Should().Be(4);
        result.RuntimeMaxThreads.Should().Be(32);
        result.RuntimeActiveWorkerThreads.Should().Be(32);
        result.RuntimeQueueLength.Should().Be(50);
        result.IsStarved.Should().BeTrue("saturated (active >= max) with a non-empty queue");
    }

    [Fact]
    public async Task AnalyzeAsync_NoRuntimeThreadPool_DataUnavailable()
    {
        HangDomainResult result = await Analyze(threadPool: new FakeRuntimeThreadPoolQuery());

        result.RuntimeThreadPoolDataAvailable.Should().BeFalse();
        result.IsStarved.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_NoWaitingThreads_HealthScoreStaysHigh()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("MyApp.Worker.DoWork"));

        HangDomainResult result = await Analyze(threads: threads);

        result.HealthScore.Should().Be(100);
    }

    [Fact]
    public async Task AnalyzeAsync_HighWaitingPercentage_ReducesHealthScore()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("System.Threading.Monitor.Wait"));

        HangDomainResult result = await Analyze(threads: threads);

        // 100% of alive threads waiting -- >= 80% bucket, -40 points.
        result.HealthScore.Should().Be(60);
    }

    [Fact]
    public async Task AnalyzeAsync_NoCandidatesOrThreads_ReturnsEmptyResult()
    {
        HangDomainResult result = await Analyze();

        result.TotalAliveThreads.Should().Be(0);
        result.TotalTasks.Should().Be(0);
        result.WaitingPercent.Should().Be(0.0);
        result.HealthScore.Should().Be(100);
    }

    [Fact]
    public void HangAnalyzer_CreateWorkerInstance_ReturnsFreshInstance()
    {
        HangAnalyzer primary = new();
        IHeapIndexScanParticipant worker = ((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();

        worker.Should().NotBeNull();
        worker.Should().NotBeSameAs(primary);
        worker.Should().BeOfType<HangAnalyzer>();
    }

    [Fact]
    public void HangAnalyzer_MergePartial_ConcatenatesCandidatesFromBothWorkers()
    {
        // Live self-attached process heap: OnHeapEntry does real ClrMD type resolution
        // (heap.GetTypeByMethodTable), so this can't be driven by fakes the way the analyzer's own
        // classification logic can -- same constraint prior batches' dump-side query
        // characterization tests hit (segment/root/handle data can't be hand-crafted either).
        DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        ClrRuntime runtime = dataTarget.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        try
        {
            ulong relevantMt = FindFirstRelevantMethodTable(heap, out ulong sampleAddress, out ulong sampleSize);
            relevantMt.Should().NotBe(0, "a live .NET test-host process always has some Task-shaped object on its heap");

            RuntimeAnalysisContext legacyContextRuntimeCtx = new() { Runtime = runtime, Cache = new DumpDetective.Analysis.Cache.HeapAnalysisCache(), AnalysisOptions = new() };

            HangAnalyzer primary = new();
            ((IHeapIndexScanParticipant)primary).BeforeHeapIndexScan(legacyContextRuntimeCtx);
            ((IHeapIndexScanParticipant)primary).OnHeapEntry(new HeapEntry(sampleAddress, relevantMt, sampleSize));

            HangAnalyzer worker = (HangAnalyzer)((IParallelHeapIndexScanParticipant)primary).CreateWorkerInstance();
            ((IHeapIndexScanParticipant)worker).BeforeHeapIndexScan(legacyContextRuntimeCtx);
            ((IHeapIndexScanParticipant)worker).OnHeapEntry(new HeapEntry(sampleAddress, relevantMt, sampleSize));

            ((IParallelHeapIndexScanParticipant)primary).MergePartial([worker]);
            ((IHeapIndexScanParticipant)primary).OnHeapIndexScanCompleted(true);

            // Thread-stack scan never ran in this test -- BuildAnalysisContext falls back to a live
            // RuntimeThreadQuery (matching the "invoked outside the pipeline" fallback every
            // quartet member already has), so this only exercises the heap-scan merge, not threads.
            DumpDetective.Sdk.Analysis.AnalysisContext legacyContext = ((ICustomizesAnalysisContext)primary).BuildAnalysisContext(legacyContextRuntimeCtx);
            primary.AnalyzeAsync(legacyContext, CancellationToken.None).GetAwaiter().GetResult();
            DumpDetective.Core.Models.AnalyzerDomainResult result = (DumpDetective.Core.Models.AnalyzerDomainResult)primary.LastResult!;
            var hangResult = (HangDomainResult)result;

            (hangResult.TotalTasks + hangResult.QueuedWorkItems + hangResult.TotalTaskContinuations)
                .Should().BeGreaterThanOrEqualTo(2, "both the primary's and the worker's candidate should have been merged in");
        }
        finally
        {
            dataTarget.Dispose();
        }
    }

    private static ulong FindFirstRelevantMethodTable(ClrHeap heap, out ulong sampleAddress, out ulong sampleSize)
    {
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is null) continue;
            string name = obj.Type.Name ?? "";
            if (AsyncTypeProfile.FromTypeName(name).IsPotentiallyRelevant)
            {
                sampleAddress = obj.Address;
                sampleSize = obj.Size;
                return obj.Type.MethodTable;
            }
        }
        sampleAddress = 0;
        sampleSize = 0;
        return 0;
    }

    private static HeapObjectRef Candidate(ulong address, string typeName) =>
        new(address, SdkTypeRefFactory.FromName(typeName, null), Size: 24, TypeDisplayName: typeName);

    private static RuntimeThreadRef Thread(uint osId, bool isAlive, int lockCount = 0) =>
        new(
            Thread: new ThreadRef { OsThreadId = osId, ManagedThreadId = (int)osId, Fidelity = MatchFidelity.Exact },
            IsGCSuspendPending: false,
            StackRootCount: 0,
            IsAlive: isAlive,
            LockCount: lockCount);

    private static ThreadStackFrameRef Frame(string signature) =>
        new(IsManagedMethod: true, HasMethod: true, DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false,
            IsReadyToRun: false, MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0,
            MethodDisplayName: signature, FrameName: signature, RawMethodSignature: signature);

    private static async Task<HangDomainResult> Analyze(
        FakeRuntimeThreadQuery? threads = null,
        HeapObjectRef[]? candidates = null,
        FakeDumpObjectFieldQuery? fields = null,
        FakeRuntimeThreadPoolQuery? threadPool = null)
    {
        HangAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            RuntimeThreads = threads ?? new FakeRuntimeThreadQuery(),
            HeapObjects = new PrecomputedHeapObjectStream(candidates ?? []),
            DumpObjectFields = fields ?? new FakeDumpObjectFieldQuery(),
            RuntimeThreadPool = threadPool ?? new FakeRuntimeThreadPoolQuery(),
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (HangDomainResult)analyzer.LastResult!;
    }

    private sealed class FakeRuntimeThreadQuery : IRuntimeThreadQuery
    {
        private readonly List<RuntimeThreadRef> _threads = new();
        private readonly Dictionary<uint, List<ThreadStackFrameRef>> _framesByOsId = new();

        public void AddThread(RuntimeThreadRef thread, params ThreadStackFrameRef[] frames)
        {
            _threads.Add(thread);
            _framesByOsId[thread.Thread.OsThreadId] = new List<ThreadStackFrameRef>(frames);
        }

        public IEnumerable<RuntimeThreadRef> EnumerateThreads() => _threads;

        public IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread) =>
            _framesByOsId.TryGetValue(thread.Thread.OsThreadId, out var frames) ? frames : [];
    }

    private sealed class FakeDumpObjectFieldQuery : IDumpObjectFieldQuery
    {
        public IReadOnlyList<InstanceFieldDescriptor> EnumerateInstanceFields(ulong address) => [];
        public IReadOnlyList<ThreadStackFrameRef> EnumerateExceptionStackTrace(ulong exceptionAddress, int maxFrames) => [];

        public bool HasDelegateField(ulong address) => false;

        private readonly Dictionary<(ulong Address, string FieldName), FieldValue> _values = new();

        public void SetInt32(ulong address, string fieldName, int value) => _values[(address, fieldName)] = FieldValue.ForInt32(value);

        public bool TryReadField(ulong address, string fieldName, out FieldValue value) =>
            _values.TryGetValue((address, fieldName), out value);

        public bool TryGetStringValue(ulong address, out string? value)
        {
            value = null;
            return false; // not exercised by this analyzer
        }

        public bool TryGetStringValue(ulong address, int maxLength, out string? value) => TryGetStringValue(address, out value);

        public bool TryGetFieldTypeName(ulong address, string fieldName, out string? typeName)
        {
            typeName = null;
            return false; // not exercised by this analyzer
        }

        public IReadOnlyList<(string Name, ulong TargetAddress)> EnumerateObjectReferenceFields(ulong address) => [];

        public bool TryReadNestedStructField(ulong address, string outerFieldName, string innerFieldName, out FieldValue value)
        {
            value = FieldValue.Null;
            return false; // not exercised by this analyzer
        }
    }

    private sealed class FakeRuntimeThreadPoolQuery : IRuntimeThreadPoolQuery
    {
        private RuntimeThreadPoolInfo? _info;
        public void SetInfo(RuntimeThreadPoolInfo info) => _info = info;

        public bool TryGetThreadPoolInfo(out RuntimeThreadPoolInfo info)
        {
            if (_info is null)
            {
                info = default;
                return false;
            }
            info = _info.Value;
            return true;
        }
    }
}
