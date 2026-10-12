using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.Analyzers.Leaks;
using DumpDetective.Analysis.Models;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Characterization tests for the Tier-1+Tier-2 retyping of <see cref="EventLeakAnalyzer"/>'s
/// capability-driven entry point (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// The bulk of this analyzer's pure logic (severity scoring, correlation folds, subscriber-count
/// histogram, timer/property-changed classification, accumulator merging) is already covered by
/// the pre-existing <c>EventLeakAnalyzerAccuracyTests</c>, unchanged by this retyping — it now
/// targets <see cref="EventLeakAnalyzer"/> for the members that moved there. This
/// file covers only what's new: the capability-driven <c>AnalyzeAsync</c> entry point, the
/// dominator-tree-vs-type-size-map retained-bytes backfill, and the root-path evidence retarget.
/// </summary>
public sealed class EventLeakAnalyzerRetypingCharacterizationTests
{
    [Fact]
    public async Task AnalyzeAsync_NoGroupedLeaks_ReturnsEmptyResult()
    {
        EventLeakDomainResult result = await Analyze(
            new FakeHeapTypeStatisticsQuery(), dominators: null, new FakeHeapRootPathQuery(),
            new EventLeakAnalyzerOptions(new EventLeakOptions(), [], EventsScanned: 5, PublisherInstances: 2,
                CandidatePublisherTypeCount: 3, LeakingPublisherTypeCount: 0));

        result.TotalEventLeakInstances.Should().Be(0);
        result.TotalEventsScanned.Should().Be(5);
        result.CleanPublisherTypeCount.Should().Be(3);
    }

    [Fact]
    public async Task AnalyzeAsync_SubscriberWithoutDominatorTree_FallsBackToTypeSizeAverage()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Subscriber", instanceCount: 4, totalSize: 400); // avg 100 bytes/instance

        var group = MakeGroup("MyApp.Publisher", "Changed", subscriberType: "MyApp.Subscriber", subscriberAddress: 0x2000);

        EventLeakDomainResult result = await Analyze(typeStats, dominators: null, new FakeHeapRootPathQuery(),
            new EventLeakAnalyzerOptions(new EventLeakOptions(), [group], 1, 1, 1, 1));

        SubscriberInfo subscriber = group.Instances[0].Subscribers[0];
        subscriber.SubscriberSize.Should().Be(100UL);
        subscriber.SubscriberSizeIsExact.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_SubscriberWithDominatorTree_PrefersExactRetainedBytes()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        typeStats.SetType("MyApp.Subscriber", instanceCount: 4, totalSize: 400); // avg 100 -- should lose to exact

        var dominators = new FakeHeapDominatorQuery();
        dominators.SetRetainedSize(0x2000, 9_999);

        var group = MakeGroup("MyApp.Publisher", "Changed", subscriberType: "MyApp.Subscriber", subscriberAddress: 0x2000);

        EventLeakDomainResult result = await Analyze(typeStats, dominators, new FakeHeapRootPathQuery(),
            new EventLeakAnalyzerOptions(new EventLeakOptions(), [group], 1, 1, 1, 1));

        SubscriberInfo subscriber = group.Instances[0].Subscribers[0];
        subscriber.SubscriberSize.Should().Be(9_999UL);
        subscriber.SubscriberSizeIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeAsync_InstanceWithNoRootHint_GetsRootPathEvidenceFromCapability()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        var rootPaths = new FakeHeapRootPathQuery();
        rootPaths.SetPath(0x1000, "Static: MyApp.Cache.Instance -> MyApp.Publisher@0x1000");

        var group = MakeGroup("MyApp.Publisher", "Changed", subscriberType: "MyApp.Subscriber", subscriberAddress: 0x2000, publisherAddress: 0x1000);

        EventLeakDomainResult result = await Analyze(typeStats, dominators: null, rootPaths,
            new EventLeakAnalyzerOptions(new EventLeakOptions(), [group], 1, 1, 1, 1));

        EventLeakInstanceSnapshot instance = result.TopLeakInstances.Should().ContainSingle().Subject;
        instance.Evidence.Should().NotBeNull();
        instance.Evidence!.PublisherRootPath.Should().Contain("Static:");
    }

    [Fact]
    public async Task AnalyzeAsync_InstanceWithRootHintAlready_SkipsRootPathSearch()
    {
        var typeStats = new FakeHeapTypeStatisticsQuery();
        var rootPaths = new FakeHeapRootPathQuery();
        // No path registered -- if the analyzer calls TryFindRootPath despite the existing hint,
        // this fake returns false/null, distinguishing "skipped" from "searched and found nothing".

        var group = MakeGroup("MyApp.Publisher", "Changed", subscriberType: "MyApp.Subscriber", subscriberAddress: 0x2000, publisherAddress: 0x1000);
        group.Instances[0].RootHint = "StrongHandle";

        EventLeakDomainResult result = await Analyze(typeStats, dominators: null, rootPaths,
            new EventLeakAnalyzerOptions(new EventLeakOptions(), [group], 1, 1, 1, 1));

        EventLeakInstanceSnapshot instance = result.TopLeakInstances.Should().ContainSingle().Subject;
        instance.RootHint.Should().Be("StrongHandle");
        instance.Evidence!.PublisherRootPath.Should().BeNull();
    }

    private static EventGroupInfo MakeGroup(
        string publisherType, string eventFieldName, string subscriberType, ulong subscriberAddress, ulong publisherAddress = 0x1000)
    {
        var subscriber = new SubscriberInfo { Address = subscriberAddress, Type = subscriberType, MethodName = "OnChanged" };
        var leak = new EventLeakInfo
        {
            PublisherAddress = publisherAddress,
            PublisherType = publisherType,
            EventFieldName = eventFieldName,
            SubscriberCount = 1,
            Subscribers = [subscriber],
            SeverityScore = 10,
        };
        return new EventGroupInfo
        {
            PublisherType = publisherType,
            EventFieldName = eventFieldName,
            InstanceCount = 1,
            TotalSubscribers = 1,
            MaxSubscribers = 1,
            MinSubscribers = 1,
            Instances = [leak],
            AllSubscriberTypeCounts = new Dictionary<string, int> { [subscriberType] = 1 },
            AllSubscriberMethodCounts = new Dictionary<(string, string?), int> { [(subscriberType, "OnChanged")] = 1 },
            SubscriberCountBuckets = new int[8],
        };
    }

    private static async Task<EventLeakDomainResult> Analyze(
        FakeHeapTypeStatisticsQuery typeStatistics, FakeHeapDominatorQuery? dominators, FakeHeapRootPathQuery rootPaths,
        EventLeakAnalyzerOptions analyzerOptions)
    {
        EventLeakAnalyzer analyzer = new();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() {
            Observations = NoOpObservationSink.Instance,
            ArtifactId = new ArtifactId("test"),
            HeapTypeStatistics = typeStatistics,
            HeapDominators = dominators,
            HeapRootPaths = rootPaths,
            AnalyzerOptions = analyzerOptions,
        };

        await analyzer.AnalyzeAsync(context, CancellationToken.None);
        return (EventLeakDomainResult)analyzer.LastResult!;
    }

    [Fact]
    public async Task EmitObservations_OneSubscriptionFactPerGroupAndSubscriberType_PublisherFirst_WithEveryTypeCounted()
    {
        EventLeakGroupSnapshot Group(string publisher, string eventField, params (string Type, int Count)[] subscribers) => new(
            publisher, eventField, IsStatic: false, MaxSeverityScore: 1, InstanceCount: 1, TotalSubscribers: subscribers.Sum(s => s.Count),
            AverageSubscribers: 1, MinSubscribers: 1, MaxSubscribers: 1,
            TopSubscriberTypes: subscribers.Select(s => new NameCountEntry(s.Type, s.Count)).ToList());
        var result = new EventLeakDomainResult(
            TotalEventLeakInstances: 2, TotalSubscribers: 4, StaticEventLeakCount: 0, InstanceEventLeakCount: 2,
            TopLeakGroups:
            [
                Group("App.Publisher", "Changed", ("App.ViewA", 3), ("App.ViewB", 1)),
                Group("App.Publisher", "Closed", ("App.ViewA", 5)),
                Group("App.Empty", "Nothing"),
            ]);
        var sink = new CollectingObservationSink();
        DumpDetective.Sdk.Analysis.AnalysisContext context = new() { Observations = sink, ArtifactId = new ArtifactId("test") };

        await EventLeakAnalyzer.EmitObservationsAsync(context, result, CancellationToken.None);

        var facts = sink.Observations.Where(o => o.ObservationType == "event.subscription").ToList();
        facts.Should().HaveCount(3, "one per (group, subscriber type), none for a group with no subscriber types");
        facts.Should().OnlyContain(o => o.Subjects.Count == 2 && o.Subjects[0].JoinKey == "App.Publisher");
        facts.Select(o => (o.Evidence[0].Description, o.Subjects[1].JoinKey, o.Measures["subscription.count"].Value))
            .Should().BeEquivalentTo([("Changed", "App.ViewA", 3d), ("Changed", "App.ViewB", 1d), ("Closed", "App.ViewA", 5d)]);
    }

    private sealed class FakeHeapTypeStatisticsQuery : IHeapTypeStatisticsQuery
    {
        private readonly Dictionary<string, HeapTypeStatistics> _stats = new(StringComparer.Ordinal);

        public void SetType(string typeName, long instanceCount, ulong totalSize) =>
            _stats[typeName] = new HeapTypeStatistics(
                SdkTypeRefFactory.FromName(typeName, null),
                InstanceCount: instanceCount,
                TotalSize: totalSize,
                LohCount: 0,
                LohSize: 0,
                Gen0Count: 0,
                Gen1Count: 0,
                Gen2Count: 0,
                Gen2TotalSize: 0,
                IsFinalizableType: false);

        public IReadOnlyDictionary<string, HeapTypeStatistics> GetTypeStatistics() => _stats;
        public bool HasExactGenerationData => true;
        public bool HasTypeShapeData => true;
        public (ulong Gen0Bytes, ulong Gen1Bytes, ulong Gen2Bytes) GetExactGenerationByteTotals() => (0, 0, 0);
        public long? ExactObjectCount => null;
        public ulong GetTotalIndexedBytes() => 0;
        public IReadOnlyList<TypeRef>? TryGetDistinctTypes() => null;
        public IReadOnlyList<long>? TryGetGlobalSizeBuckets() => null;
    }

    private sealed class FakeHeapDominatorQuery : IHeapDominatorQuery
    {
        private readonly Dictionary<ulong, ulong> _retained = new();
        public void SetRetainedSize(ulong address, ulong bytes) => _retained[address] = bytes;

        public bool TryGetRetainedSize(ulong address, out ulong retainedBytes) => _retained.TryGetValue(address, out retainedBytes);
        public bool TryGetImmediateDominator(ulong address, out ulong dominatorAddress) { dominatorAddress = 0; return false; }
        public bool TryGetThreadRetainedSize(uint osThreadId, out ulong retainedBytes) { retainedBytes = 0; return false; }
        public IEnumerable<HeapObjectRef> EnumerateRetainedObjects(ulong rootAddress) => [];
        public bool TryGetRetainedBytesByType(TypeRef type, out ulong retainedBytes) { retainedBytes = 0; return false; }
    }

    private sealed class FakeHeapRootPathQuery : IHeapRootPathQuery
    {
        private readonly Dictionary<ulong, string> _paths = new();
        public void SetPath(ulong address, string formattedPath) => _paths[address] = formattedPath;

        public bool TryFindRootPath(ulong targetAddress, RootPathSearchOptions options, out string? formattedPath, out bool searchTruncated)
        {
            searchTruncated = false;
            return _paths.TryGetValue(targetAddress, out formattedPath);
        }

        public bool TryFindRootPathHops(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<string>? hopTypeNames, out bool searchTruncated)
        {
            rootKind = null;
            hopTypeNames = null;
            searchTruncated = false;
            return false;
        }

        public bool TryFindRootPathAddresses(ulong targetAddress, RootPathSearchOptions options, out string? rootKind, out IReadOnlyList<ulong>? hopAddresses, out bool searchTruncated)
        {
            rootKind = null;
            hopAddresses = null;
            searchTruncated = false;
            return false;
        }
    }
}
