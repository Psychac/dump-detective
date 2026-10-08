using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Threads;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization test for the Tier-1+Tier-2 batch (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md):
/// <see cref="ThreadAnalyzer"/> had no pre-existing test coverage at all before this retyping. Uses
/// a fake, list-backed <see cref="IRuntimeThreadQuery"/> (the same shape
/// <c>TimerLeakAnalyzerRetypingCharacterizationTests</c>/<c>WeakReferenceAnalyzerRetypingCharacterizationTests</c>
/// established) rather than a live/real heap — this analyzer's retyped logic is purely
/// capability-interface-driven, so every branch is exactly controllable without ClrMD at all.
/// </summary>
public sealed class ThreadAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_AliveAndDeadThreads_CountedSeparately()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true));
        threads.AddThread(Thread(osId: 2, isAlive: false));

        ThreadDomainResult result = await Analyze(threads);

        result.TotalThreadCount.Should().Be(2);
        result.AliveThreadCount.Should().Be(1);
        result.InactiveThreadCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadWithLock_TrackedAndSortedByLockCountDescending()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, lockCount: 1));
        threads.AddThread(Thread(osId: 2, isAlive: true, lockCount: 5));

        ThreadDomainResult result = await Analyze(threads);

        result.LockHoldingThreadCount.Should().Be(2);
        result.TopLockedThreads.Should().HaveCount(2);
        result.TopLockedThreads![0].LockCount.Should().Be(5, "sorted descending by lock count");
        result.TopLockedThreads[1].LockCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadWithActiveException_TrackedAndDistributed()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, hasException: true, exceptionType: "System.NullReferenceException", exceptionMessage: "Object reference not set"));

        ThreadDomainResult result = await Analyze(threads);

        result.ThreadsWithActiveExceptionsCount.Should().Be(1);
        result.ExceptionTypeDistribution.Should().ContainKey("System.NullReferenceException").WhoseValue.Should().Be(1);
        result.ThreadsWithActiveExceptions.Should().ContainSingle().Which.ExceptionMessage.Should().Be("Object reference not set");
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadBlockedOnMonitorWait_ClassifiedAndCounted()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("System.Threading.Monitor.Wait"));

        ThreadDomainResult result = await Analyze(threads);

        result.BlockedThreadCount.Should().Be(1);
        result.WaitPatternBreakdown.Should().ContainKey("MonitorWait").WhoseValue.Should().Be(1);
        result.TopBlockedThreads.Should().ContainSingle();
    }

    [Fact]
    public async Task AnalyzeAsync_NonBlockedUserThread_TracksActiveThreadHotspot()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("MyApp.Worker.DoWork"));

        ThreadDomainResult result = await Analyze(threads);

        result.TopActiveThreadHotspots.Should().ContainSingle(h => h.Name == "MyApp.Worker.DoWork");
    }

    [Fact]
    public async Task AnalyzeAsync_HotspotTracking_SkipsFrameworkFramesForFirstNonFrameworkFrame()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("System.Collections.Generic.List`1.Add"), Frame("MyApp.Worker.DoWork"));

        ThreadDomainResult result = await Analyze(threads);

        result.TopActiveThreadHotspots.Should().ContainSingle(h => h.Name == "MyApp.Worker.DoWork");
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadpoolWorkerFlag_CountsAsThreadPoolThread()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, isThreadpoolWorker: true));

        ThreadDomainResult result = await Analyze(threads);

        result.ThreadPoolWorkerCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadpoolWorkerFrameHeuristic_CountsAsThreadPoolThreadEvenWithoutFlag()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, isThreadpoolWorker: false), Frame("System.Threading.PortableThreadPool.WorkerThread"));

        ThreadDomainResult result = await Analyze(threads);

        result.ThreadPoolWorkerCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_FinalizerThread_CapturedWithFrames()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 7, isAlive: true, isFinalizer: true, managedThreadId: 3, lockCount: 2), Frame("System.Object.Finalize"));

        ThreadDomainResult result = await Analyze(threads);

        result.FinalizerThreadCount.Should().Be(1);
        result.FinalizerOsThreadId.Should().Be(7u);
        result.FinalizerManagedThreadId.Should().Be(3u);
        result.FinalizerLockCount.Should().Be(2);
        result.FinalizerFrames.Should().ContainSingle(f => f == "System.Object.Finalize");
        result.FinalizerThreadBlocked.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_AsyncStateMachineFrames_TracksChainDepth()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true),
            Frame("MyApp.Foo+<DoAsync>d__1.MoveNext()"),
            Frame("MyApp.Bar+<CallAsync>d__2.MoveNext()"));

        ThreadDomainResult result = await Analyze(threads);

        result.AsyncChainThreadCount.Should().Be(1);
        result.MaxAsyncChainDepth.Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeAsync_BackgroundThread_CountedSeparately()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, isBackground: true));

        ThreadDomainResult result = await Analyze(threads);

        result.BackgroundThreadCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_GcThread_ExcludedFromActiveThreadHotspotsAndCountedAsGc()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, isGc: true), Frame("MyApp.Worker.DoWork"));

        ThreadDomainResult result = await Analyze(threads);

        result.GcThreadCount.Should().Be(1);
        result.TopActiveThreadHotspots.Should().BeEmpty("GC threads are excluded from the Active Processing hotspot group");
    }

    [Fact]
    public async Task AnalyzeAsync_AliveThreadWithNoLockWaitOrException_GoesToOtherThreads()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true), Frame("MyApp.Worker.Idle"));

        ThreadDomainResult result = await Analyze(threads);

        result.OtherThreads.Should().ContainSingle();
        result.LockHoldingThreadCount.Should().Be(0);
        result.BlockedThreadCount.Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeAsync_StackSizeBytes_FeedsStackMemorySummary()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, stackSizeBytes: 1_000_000));
        threads.AddThread(Thread(osId: 2, isAlive: true, stackSizeBytes: 2_000_000));

        ThreadDomainResult result = await Analyze(threads);

        result.StackMemorySummary.Should().NotBeNull();
        result.StackMemorySummary!.SampleCount.Should().Be(2);
        result.StackMemorySummary.TotalBytes.Should().Be(3_000_000);
        result.StackMemorySummary.MaxBytes.Should().Be(2_000_000);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadStateAndGcModeDisplay_PassedThroughVerbatim()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true) with { ThreadStateDisplay = "TS_Background | TS_CoInitialized", GcModeDisplay = "Cooperative" });

        ThreadDomainResult result = await Analyze(threads);

        result.ThreadStateDistribution.Should().ContainKey("TS_Background | TS_CoInitialized");
        result.GcModeDistribution.Should().ContainKey("Cooperative");
    }

    [Fact]
    public async Task AnalyzeAsync_AppDomainName_DistributionUsesPlaceholderWhenNull()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, appDomainName: null));

        ThreadDomainResult result = await Analyze(threads);

        result.AppDomainDistribution.Should().ContainKey("<No AppDomain>");
    }

    [Fact]
    public async Task AnalyzeAsync_EmitsOneAppDomainFactPerNamedDomain_AndNoneForThreadsWithoutADomain()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true, appDomainName: "DefaultDomain"));
        threads.AddThread(Thread(osId: 2, isAlive: false, appDomainName: "DefaultDomain"));
        threads.AddThread(Thread(osId: 3, isAlive: true, appDomainName: "/LM/W3SVC/1/ROOT"));
        threads.AddThread(Thread(osId: 4, isAlive: true, appDomainName: null));

        IReadOnlyList<Observation> observations = await AnalyzeCollectingObservations(threads, new FakeThreadRetentionDominatorQuery());

        IReadOnlyList<Observation> domainFacts = observations.Where(o => o.ObservationType == "thread.appdomain-population").ToList();
        domainFacts.Should().HaveCount(2);
        domainFacts.Should().OnlyContain(o => o.Subjects.Count == 1 && o.Subjects[0] is AppDomainRef);
        domainFacts.Single(o => o.Subjects[0].JoinKey == "DefaultDomain").Measures["thread.count"].Value.Should().Be(2);
        domainFacts.Single(o => o.Subjects[0].JoinKey == "/LM/W3SVC/1/ROOT").Measures["thread.count"].Value.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_NoThreads_ReturnsEmptyResult()
    {
        ThreadDomainResult result = await Analyze(new FakeRuntimeThreadQuery());

        result.TotalThreadCount.Should().Be(0);
        result.BlockedThreadRatio.Should().Be(0.0);
        result.StackMemorySummary.Should().BeNull();
    }

    [Fact]
    public void CountMoveNextDepthFromSignatures_CountsCaseInsensitiveMoveNextOccurrences()
    {
        ThreadAnalyzer.CountMoveNextDepthFromSignatures(["Foo.MOVENEXT()", "Bar.Something()", "Baz.MoveNext()"]).Should().Be(2);
    }

    [Fact]
    public void BuildStackMemorySummary_EmptySamples_ReturnsNull()
    {
        ThreadAnalyzer.BuildStackMemorySummary([]).Should().BeNull();
    }

    [Fact]
    public void WaitPatternsForTesting_ContainsExpectedCategories()
    {
        ThreadAnalyzer.WaitPatternsForTesting.Should().Contain(p => p.Category == "MonitorWait");
        ThreadAnalyzer.WaitPatternsForTesting.Should().Contain(p => p.Category == "TaskBlocking");
    }

    [Fact]
    public async Task AnalyzeAsync_WithThreadRetention_EmitsRetainedBytesOnlyForThreadsTheTreeKnows()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true));
        threads.AddThread(Thread(osId: 2, isAlive: true));
        var dominators = new FakeThreadRetentionDominatorQuery();
        dominators.SetRetainedBytes(osThreadId: 1, bytes: 4096);

        IReadOnlyList<Observation> observations = await AnalyzeCollectingObservations(threads, dominators);

        Observation retained = observations.Should().ContainSingle(o => o.ObservationType == "thread.retained-bytes").Which;
        retained.Subjects.Should().ContainSingle().Which.JoinKey.Should().Be("1");
        retained.Measures["retained.bytes"].Value.Should().Be(4096);
        retained.Provenance.CapabilitiesUsed.Should().Contain(CapabilityVocabulary.HeapDominators);
    }

    [Fact]
    public async Task AnalyzeAsync_ThreadWithNoStackRoots_EmitsNoRetentionObservationRatherThanZero()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 7, isAlive: true));

        IReadOnlyList<Observation> observations = await AnalyzeCollectingObservations(threads, new FakeThreadRetentionDominatorQuery());

        observations.Should().NotContain(o => o.ObservationType == "thread.retained-bytes");
        observations.Should().Contain(o => o.ObservationType == "thread.state");
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutDominatorTree_EmitsNoRetentionObservations()
    {
        var threads = new FakeRuntimeThreadQuery();
        threads.AddThread(Thread(osId: 1, isAlive: true));

        IReadOnlyList<Observation> observations = await AnalyzeCollectingObservations(threads, dominators: null);

        observations.Should().NotContain(o => o.ObservationType == "thread.retained-bytes");
    }

    private static async Task<IReadOnlyList<Observation>> AnalyzeCollectingObservations(
        FakeRuntimeThreadQuery threads, IHeapDominatorQuery? dominators)
    {
        var sink = new CollectingObservationSink();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = sink,
            ArtifactId = new ArtifactId("test"),
            RuntimeThreads = threads,
            HeapDominators = dominators,
        };

        await new ThreadAnalyzer().AnalyzeAsync(context, CancellationToken.None);
        return sink.Observations;
    }

    private sealed class FakeThreadRetentionDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<uint, ulong> _retainedByThread = new();
        public void SetRetainedBytes(uint osThreadId, ulong bytes) => _retainedByThread[osThreadId] = bytes;

        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) => _retainedByThread.TryGetValue(osThreadId, out retainedBytes);
        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) => [];
        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) { retainedBytes = 0; return false; }
    }

    private static RuntimeThreadRef Thread(
        uint osId, bool isAlive, int lockCount = 0, bool hasException = false, string? exceptionType = null,
        string? exceptionMessage = null, bool isThreadpoolWorker = false, bool isFinalizer = false,
        int? managedThreadId = null, bool isBackground = false, bool isGc = false,
        ulong stackSizeBytes = 0, string? appDomainName = "MyApp.AppDomain") =>
        new(
            Thread: new ThreadRef { OsThreadId = osId, ManagedThreadId = managedThreadId, Fidelity = MatchFidelity.Exact },
            IsGCSuspendPending: false,
            StackRootCount: 0,
            IsAlive: isAlive,
            LockCount: lockCount,
            IsGc: isGc,
            IsFinalizer: isFinalizer,
            IsThreadpoolWorker: isThreadpoolWorker,
            IsCompletionPortThread: false,
            IsBackground: isBackground,
            ThreadStateDisplay: "0",
            GcModeDisplay: "Cooperative",
            AppDomainName: appDomainName,
            HasCurrentException: hasException,
            ExceptionTypeName: exceptionType,
            ExceptionMessage: exceptionMessage,
            StackSizeBytes: stackSizeBytes);

    private static ThreadStackFrameRef Frame(string signature) =>
        new(IsManagedMethod: true, HasMethod: true, DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false,
            IsReadyToRun: false, MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0,
            MethodDisplayName: signature, FrameName: signature, RawMethodSignature: signature);

    private static async Task<ThreadDomainResult> Analyze(FakeRuntimeThreadQuery threads)
    {
        ThreadAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            RuntimeThreads = threads,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (ThreadDomainResult)analyzer.LastResult!;
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
}
