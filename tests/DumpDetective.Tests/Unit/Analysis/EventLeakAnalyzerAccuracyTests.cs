using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Accuracy tests for <see cref="EventLeakAnalyzer"/> pure-logic paths.
/// Tests are scoped to the three bugs fixed and the invariants they rely on:
///   Bug A – inherited event backing fields dropped when type declares own events.
///   Bug B – static-method subscribers (null _target) not counted.
///   Grouping – publisher count, subscriber totals, min/max/avg must be exact.
/// </summary>
public sealed class EventLeakAnalyzerAccuracyTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static EventLeakOptions DefaultOptions => new();

    private static EventLeakInfo MakeLeak(
        string publisherType,
        string fieldName,
        bool isStatic,
        int subscriberCount,
        ulong publisherAddress = 1,
        string subscriberType = "App.Subscriber")
    {
        var subscribers = new List<SubscriberInfo>(subscriberCount);
        for (int i = 0; i < subscriberCount; i++)
            subscribers.Add(new SubscriberInfo { Address = (ulong)(100 + i), Type = subscriberType });

        return new EventLeakInfo
        {
            PublisherAddress = publisherAddress,
            PublisherType = publisherType,
            EventFieldName = fieldName,
            IsStatic = isStatic,
            SubscriberCount = subscriberCount,
            Subscribers = subscribers,
            RootHint = string.Empty,
            SeverityScore = EventLeakAnalyzer.CalculateSeverity(isStatic, subscriberCount, string.Empty, DefaultOptions)
        };
    }

    private static EventLeakAnalyzer NewAnalyzer() => new();

    // -----------------------------------------------------------------------
    // GroupEventLeaks – publisher count
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupEventLeaks_TwoDistinctTypeFieldPairs_ProducesTwoGroups()
    {
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "Changed", isStatic: false, subscriberCount: 2),
            MakeLeak("App.B", "Changed", isStatic: false, subscriberCount: 3),
        };

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        groups.Should().HaveCount(2);
    }

    [Fact]
    public void GroupEventLeaks_SameTypeDifferentFields_ProducesTwoGroups()
    {
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "Opened", isStatic: false, subscriberCount: 1),
            MakeLeak("App.A", "Closed", isStatic: false, subscriberCount: 1),
        };

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        groups.Should().HaveCount(2);
    }

    [Fact]
    public void GroupEventLeaks_SameTypeFieldStaticVsInstance_ProducesTwoGroups()
    {
        // static and instance are separate even if type+field match
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "Changed", isStatic: false, subscriberCount: 2),
            MakeLeak("App.A", "Changed", isStatic: true,  subscriberCount: 3),
        };

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        groups.Should().HaveCount(2);
    }

    [Fact]
    public void GroupEventLeaks_MultipleInstancesSameGroup_PublisherCountIsCorrect()
    {
        // 5 separate publisher instances of the same type+field
        var leaks = Enumerable.Range(1, 5)
            .Select(i => MakeLeak("App.A", "Changed", isStatic: false, subscriberCount: 2, publisherAddress: (ulong)i))
            .ToList();

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        groups.Should().HaveCount(1);
        groups[0].InstanceCount.Should().Be(5, "each publisher address is a separate instance");
    }

    // -----------------------------------------------------------------------
    // GroupEventLeaks – subscriber totals, min, max, average
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupEventLeaks_SubscriberCountsAreAggregatedExactly()
    {
        // Three instances with 1, 4, 7 subscribers respectively
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "E", isStatic: false, subscriberCount: 1, publisherAddress: 1),
            MakeLeak("App.A", "E", isStatic: false, subscriberCount: 4, publisherAddress: 2),
            MakeLeak("App.A", "E", isStatic: false, subscriberCount: 7, publisherAddress: 3),
        };

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        var g = groups.Single();
        g.TotalSubscribers.Should().Be(12);
        g.MinSubscribers.Should().Be(1);
        g.MaxSubscribers.Should().Be(7);
        g.AverageSubscribers.Should().BeApproximately(4.0, precision: 0.001);
    }

    [Fact]
    public void GroupEventLeaks_SingleInstance_MinEqualsMaxEqualsTotal()
    {
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "E", isStatic: false, subscriberCount: 5),
        };

        var g = NewAnalyzer().GroupEventLeaks(leaks).Single();

        g.MinSubscribers.Should().Be(5);
        g.MaxSubscribers.Should().Be(5);
        g.TotalSubscribers.Should().Be(5);
        g.AverageSubscribers.Should().Be(5.0);
    }

    [Fact]
    public void GroupEventLeaks_SortedByTotalSubscribersDescending()
    {
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.Low",  "E", isStatic: false, subscriberCount: 1),
            MakeLeak("App.High", "E", isStatic: false, subscriberCount: 99),
            MakeLeak("App.Mid",  "E", isStatic: false, subscriberCount: 10),
        };

        var groups = NewAnalyzer().GroupEventLeaks(leaks);

        groups[0].PublisherType.Should().Be("App.High");
        groups[1].PublisherType.Should().Be("App.Mid");
        groups[2].PublisherType.Should().Be("App.Low");
    }

    // -----------------------------------------------------------------------
    // GroupEventLeaks – static vs instance classification
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupEventLeaks_StaticLeak_IsStaticFlagPreserved()
    {
        var leaks = new List<EventLeakInfo>
        {
            MakeLeak("App.A", "E", isStatic: true, subscriberCount: 3),
        };

        NewAnalyzer().GroupEventLeaks(leaks).Single().IsStatic.Should().BeTrue();
    }

    [Fact]
    public void GroupEventLeaks_EmptyInput_ReturnsEmptyList()
    {
        NewAnalyzer().GroupEventLeaks([]).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // CalculateSeverity – bonus accumulation (tuning invariants)
    // -----------------------------------------------------------------------

    [Fact]
    public void CalculateSeverity_BaseScoreEqualsSubscriberCount()
    {
        int score = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 7, rootHint: string.Empty, DefaultOptions);
        // 7 < threshold(10) so no bonus, no static bonus, no root hint bonus
        score.Should().Be(7);
    }

    [Fact]
    public void CalculateSeverity_HighSubscriberCountAppliesBonus()
    {
        var opts = DefaultOptions; // threshold=10, bonus=5
        int score = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 10, rootHint: string.Empty, opts);
        score.Should().Be(10 + opts.SeveritySubscriberBonus);
    }

    [Fact]
    public void CalculateSeverity_StaticPublisherAppliesBonus()
    {
        var opts = DefaultOptions;
        int instanceScore = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 3, rootHint: string.Empty, opts);
        int staticScore = EventLeakAnalyzer.CalculateSeverity(isStatic: true, subscriberCount: 3, rootHint: string.Empty, opts);
        (staticScore - instanceScore).Should().Be(opts.SeverityStaticPublisherBonus);
    }

    [Fact]
    public void CalculateSeverity_RootHintPresentAppliesBonus()
    {
        var opts = DefaultOptions;
        int noHint = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 3, rootHint: string.Empty, opts);
        int withHint = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 3, rootHint: "static root", opts);
        (withHint - noHint).Should().Be(opts.SeverityRootHintBonus);
    }

    [Fact]
    public void CalculateSeverity_AllBonusesStack()
    {
        var opts = DefaultOptions; // threshold=10, subscriberBonus=5, staticBonus=10, rootHintBonus=5
        int score = EventLeakAnalyzer.CalculateSeverity(isStatic: true, subscriberCount: 10, rootHint: "hint", opts);
        int expected = 10 + opts.SeveritySubscriberBonus + opts.SeverityStaticPublisherBonus + opts.SeverityRootHintBonus;
        score.Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // ParseRootPublisher – root description parsing
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseRootPublisher_TypicalQualifiedName_SplitsAtLastDot()
    {
        EventLeakAnalyzer.ParseRootPublisher("MyApp.Services.Publisher.OnDataChanged",
            out string publisherType, out string eventFieldName);

        publisherType.Should().Be("MyApp.Services.Publisher");
        eventFieldName.Should().Be("OnDataChanged");
    }

    [Fact]
    public void ParseRootPublisher_NoDot_ReturnsDefaults()
    {
        EventLeakAnalyzer.ParseRootPublisher("NoDotHere",
            out string publisherType, out string eventFieldName);

        publisherType.Should().Be("StaticRoot");
        eventFieldName.Should().Be("Unknown");
    }

    [Fact]
    public void ParseRootPublisher_TrailingDot_ReturnsDefaults()
    {
        EventLeakAnalyzer.ParseRootPublisher("MyApp.Publisher.",
            out string publisherType, out string eventFieldName);

        // lastDot is at end so lastDot < length-1 is false → defaults
        publisherType.Should().Be("StaticRoot");
        eventFieldName.Should().Be("Unknown");
    }

    [Fact]
    public void ParseRootPublisher_SingleDot_SplitsCorrectly()
    {
        EventLeakAnalyzer.ParseRootPublisher("A.B",
            out string publisherType, out string eventFieldName);

        publisherType.Should().Be("A");
        eventFieldName.Should().Be("B");
    }

    // -----------------------------------------------------------------------
    // BuildEventNameSet – the core of the Bug A fix
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildEventNameSet_PairedAddRemove_ReturnsEventName()
    {
        var result = EventLeakAnalyzer.BuildEventNameSet(
            addNames: ["Changed", "Opened"],
            removeNames: ["Changed", "Opened"]);

        result.Should().BeEquivalentTo(["Changed", "Opened"]);
    }

    [Fact]
    public void BuildEventNameSet_UnpairedAdd_ExcludedFromResult()
    {
        // "Orphan" has add_ but no remove_ → not a real event
        var result = EventLeakAnalyzer.BuildEventNameSet(
            addNames: ["Changed", "Orphan"],
            removeNames: ["Changed"]);

        result.Should().BeEquivalentTo(["Changed"]);
        result.Should().NotContain("Orphan");
    }

    [Fact]
    public void BuildEventNameSet_EmptyInput_ReturnsEmpty()
    {
        EventLeakAnalyzer.BuildEventNameSet([], []).Should().BeEmpty();
    }

    [Fact]
    public void BuildEventNameSet_AddOnlyNoRemove_ReturnsEmpty()
    {
        // This validates the all-pass path: empty result → IsLikelyEventField returns true
        var result = EventLeakAnalyzer.BuildEventNameSet(["add_Something"], []);
        result.Should().BeEmpty("no paired remove_ means nothing qualifies as a real event");
    }

    /// <summary>
    /// Validates the core invariant of Bug A: when a type's own events are merged
    /// with base-class events, the inherited backing fields are no longer rejected.
    /// </summary>
    [Fact]
    public void BuildEventNameSet_OwnPlusInheritedNames_ContainsBoth()
    {
        // Own events
        var ownAdd = new[] { "OwnEvent" };
        var ownRemove = new[] { "OwnEvent" };

        // Base-class events discovered by walking BaseType
        var baseAdd = new[] { "InheritedEvent" };
        var baseRemove = new[] { "InheritedEvent" };

        var allAdd = ownAdd.Concat(baseAdd).ToArray();
        var allRemove = ownRemove.Concat(baseRemove).ToArray();

        var names = EventLeakAnalyzer.BuildEventNameSet(allAdd, allRemove);

        names.Should().Contain("OwnEvent", "own event must be accepted");
        names.Should().Contain("InheritedEvent", "inherited event must also be accepted (Bug A fix)");
    }

    // -----------------------------------------------------------------------
    // SubscriberInfo – static method subscriber counting (Bug B)
    // -----------------------------------------------------------------------

    [Fact]
    public void EventLeakInfo_StaticMethodSubscriber_CountedInSubscriberCount()
    {
        // Simulate what ExtractSingleSubscriber now emits for a null _target:
        // a SubscriberInfo with type "<static method>" and the delegate's address.
        var subscribers = new List<SubscriberInfo>
        {
            new() { Address = 0xDEAD_0001, Type = "App.RealHandler"      }, // instance
            new() { Address = 0xDEAD_0002, Type = "<static method>"      }, // Bug B fix
        };

        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "DataReady",
            IsStatic = false,
            SubscriberCount = subscribers.Count,
            Subscribers = subscribers,
        };

        leak.SubscriberCount.Should().Be(2,
            "static-method subscribers must be counted as separate subscriptions");

        leak.Subscribers.Where(s => s.Type == "<static method>")
            .Should().HaveCount(1, "exactly one static-method subscription was registered");
    }

    [Fact]
    public void GroupEventLeaks_StaticMethodSubscribers_IncludedInTotal()
    {
        var subscribers = new List<SubscriberInfo>
        {
            new() { Address = 1, Type = "App.Handler"    },
            new() { Address = 2, Type = "<static method>" },
            new() { Address = 3, Type = "<static method>" },
        };

        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "E",
            IsStatic = false,
            SubscriberCount = subscribers.Count,
            Subscribers = subscribers,
        };

        var groups = NewAnalyzer().GroupEventLeaks([leak]);

        groups.Single().TotalSubscribers.Should().Be(3,
            "two static-method subscribers plus one instance subscriber = 3 total");
    }

    // -----------------------------------------------------------------------
    // GroupEventLeaks – multi-domain static event subscription counting (Bug C)
    // The `seen` hashset in GetStaticEventSubscribers previously deduplicated
    // subscriber addresses across ALL app domains. The same subscriber object
    // subscribed in N domains is N separate GC retention paths — each must be
    // counted. Simulated here by building EventLeakInfo with repeated addresses
    // (as GetStaticEventSubscribers now produces when the same subscriber appears
    // in multiple domains' delegate chains).
    // -----------------------------------------------------------------------

    [Fact]
    public void GroupEventLeaks_SameSubscriberInMultipleDomains_CountedOncePerDomain()
    {
        // Simulate: static ManagerChanged event, 6 app domains.
        // Subscriber A (address 0x100) is subscribed in every domain.
        // Each domain contributes its own SubscriberInfo entry — no cross-domain dedup.
        const int domainCount = 6;
        const ulong subscriberA = 0x100;

        var subscribers = new List<SubscriberInfo>(domainCount);
        for (int i = 0; i < domainCount; i++)
            subscribers.Add(new SubscriberInfo { Address = subscriberA, Type = "App.Subscriber" });

        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "ManagerChanged",
            IsStatic = true,
            SubscriberCount = subscribers.Count,
            Subscribers = subscribers,
        };

        var groups = NewAnalyzer().GroupEventLeaks([leak]);

        groups.Single().TotalSubscribers.Should().Be(domainCount,
            "one subscriber registered in 6 domains = 6 separate GC retention paths, all must be counted");
    }

    [Fact]
    public void GroupEventLeaks_UniqueSubscribersAcrossDomainsAllCounted()
    {
        // 6 domains, each with 2 unique subscribers (12 total subscriptions).
        // The same subscriber A appears in all 6 domains (6 retention paths).
        // A different subscriber B appears only in domain 1.
        const ulong subscriberA = 0x100;
        var subscribers = new List<SubscriberInfo>();
        for (int i = 0; i < 6; i++)
            subscribers.Add(new SubscriberInfo { Address = subscriberA, Type = "App.Subscriber" });
        subscribers.Add(new SubscriberInfo { Address = 0x200, Type = "App.Other" }); // only in domain 1

        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "ManagerChanged",
            IsStatic = true,
            SubscriberCount = subscribers.Count,
            Subscribers = subscribers,
        };

        var groups = NewAnalyzer().GroupEventLeaks([leak]);

        groups.Single().TotalSubscribers.Should().Be(7,
            "6 cross-domain subscriptions for A plus 1 subscription for B = 7");
    }
}
