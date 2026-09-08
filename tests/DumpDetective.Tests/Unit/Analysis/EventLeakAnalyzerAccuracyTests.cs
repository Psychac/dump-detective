using DumpDetective.Analysis.Analyzers;
using DumpDetective.Core.Models;
using DumpDetective.Core.Options;
using FluentAssertions;
using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

/// <summary>
/// Accuracy tests for <see cref="EventLeakAnalyzer"/> pure-logic paths.
/// Covers severity scoring (log-scale continuous formula, design §9) and the
/// remaining static-analysis helpers (root-publisher parsing, event-name-set
/// building, enrichment-group-key bounding, retained-bytes estimation).
/// </summary>
public sealed class EventLeakAnalyzerAccuracyTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static EventLeakOptions DefaultOptions => new();

    // -----------------------------------------------------------------------
    // CalculateSeverity — continuous subscriber-count term (design §9)
    // -----------------------------------------------------------------------

    [Fact]
    public void CalculateSeverity_ZeroSubscribers_NoBonusApplied()
    {
        int score = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 0, rootHint: string.Empty, DefaultOptions);

        score.Should().Be(0, "log2(0+1)=0 so no subscriber-count bonus applies at zero subscribers");
    }

    [Fact]
    public void CalculateSeverity_SubscriberCountLogScale_MatchesFormula()
    {
        var opts = DefaultOptions;
        int score = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 7, rootHint: string.Empty, opts);

        int expectedLogBonus = (int)(Math.Log2(7 + 1) * opts.SeveritySubscriberLogScale);
        score.Should().Be(7 + expectedLogBonus);
    }

    [Fact]
    public void CalculateSeverity_ScoreIncreasesMonotonicallyWithSubscriberCount()
    {
        var opts = DefaultOptions;
        int previous = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 0, rootHint: string.Empty, opts);
        for (int n = 1; n <= 100; n++)
        {
            int current = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: n, rootHint: string.Empty, opts);
            current.Should().BeGreaterThan(previous, $"severity must strictly increase from {n - 1} to {n} subscribers");
            previous = current;
        }
    }

    [Fact]
    public void CalculateSeverity_SubscriberCountIsContinuous_NoLargeJumpsBetweenAdjacentCounts()
    {
        // Old step-function bonus produced a discontinuity at the threshold boundary;
        // the log-scale replacement must never jump by more than a small bounded amount
        // between adjacent subscriber counts.
        var opts = DefaultOptions;
        int maxJump = 0;
        for (int n = 0; n < 200; n++)
        {
            int a = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: n, rootHint: string.Empty, opts);
            int b = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: n + 1, rootHint: string.Empty, opts);
            maxJump = Math.Max(maxJump, b - a);
        }

        maxJump.Should().BeLessThanOrEqualTo(3, "the log-scale term must not produce step-function-sized jumps between adjacent subscriber counts");
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
    public void CalculateSeverity_DisposedButSubscribedAppliesBonus()
    {
        var opts = DefaultOptions;
        int notDisposed = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 3, rootHint: string.Empty, opts, isDisposedButSubscribed: false);
        int disposed = EventLeakAnalyzer.CalculateSeverity(isStatic: false, subscriberCount: 3, rootHint: string.Empty, opts, isDisposedButSubscribed: true);

        (disposed - notDisposed).Should().Be(opts.SeverityDisposedButSubscribedBonus);
    }

    [Fact]
    public void CalculateSeverity_AllBonusesStack()
    {
        var opts = DefaultOptions;
        int score = EventLeakAnalyzer.CalculateSeverity(
            isStatic: true,
            subscriberCount: 10,
            rootHint: "hint",
            opts,
            publisherGeneration: 2,
            duplicateCount: 1,
            isDisposedButSubscribed: true,
            hasLifetimeMismatch: true,
            hasLowIncomingRefs: true);

        int expectedLogBonus = (int)(Math.Log2(10 + 1) * opts.SeveritySubscriberLogScale);
        int expected = 10 + expectedLogBonus
            + opts.SeverityStaticPublisherBonus
            + opts.SeverityRootHintBonus
            + opts.SeverityGen2PublisherBonus
            + opts.SeverityDuplicateSubscriptionBonus
            + opts.SeverityDisposedButSubscribedBonus
            + opts.SeverityLifetimeMismatchBonus
            + opts.SeverityLowIncomingRefsBonus;

        score.Should().Be(expected);
    }

    // -----------------------------------------------------------------------
    // EventLeakInfo — model-level flags
    // -----------------------------------------------------------------------

    [Fact]
    public void EventLeakInfo_StaticMethodSubscriber_CountedInSubscriberCount()
    {
        var subscribers = new List<SubscriberInfo>
        {
            new() { Address = 0xDEAD_0001, Type = "App.RealHandler" },
            new() { Address = 0xDEAD_0002, Type = "<static method>" },
        };

        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "DataReady",
            IsStatic = false,
            SubscriberCount = subscribers.Count,
            Subscribers = subscribers,
        };

        leak.SubscriberCount.Should().Be(2, "static-method subscribers must be counted as separate subscriptions");
        leak.Subscribers.Where(s => s.Type == "<static method>")
            .Should().HaveCount(1, "exactly one static-method subscription registered");
    }

    [Fact]
    public void EventLeakInfo_IsDisposedButSubscribed_FlagIsStored()
    {
        var leak = new EventLeakInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "DataReady",
            IsStatic = false,
            IsDisposedButSubscribed = true,
        };

        leak.IsDisposedButSubscribed.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // AddToAccumulator — leakingMTs tracking (P2-2, docs/analysis/phase1/eventleak-analyzer-audit.md)
    // -----------------------------------------------------------------------

    [Fact]
    public void AddToAccumulator_RecordsPublisherMethodTable_InLeakingMTs()
    {
        var acc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), EventLeakAnalyzer.GroupAccumulator>();
        var leakingMTs = new HashSet<ulong>();
        var leak = new EventLeakInfo
        {
            PublisherMethodTable = 0x1234,
            PublisherType = "App.Publisher",
            EventFieldName = "DataReady",
            IsStatic = false,
            SubscriberCount = 1,
        };

        EventLeakAnalyzer.AddToAccumulator(acc, leak, capacity: 5, leakingMTs);

        leakingMTs.Should().ContainSingle().Which.Should().Be(0x1234UL);
    }

    [Fact]
    public void AddToAccumulator_MultipleLeaksSameMethodTable_DeduplicatesInLeakingMTs()
    {
        var acc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), EventLeakAnalyzer.GroupAccumulator>();
        var leakingMTs = new HashSet<ulong>();

        for (int i = 0; i < 3; i++)
        {
            var leak = new EventLeakInfo
            {
                PublisherMethodTable = 0xAAAA,
                PublisherType = "App.Publisher",
                EventFieldName = "DataReady",
                IsStatic = false,
                SubscriberCount = 1,
            };
            EventLeakAnalyzer.AddToAccumulator(acc, leak, capacity: 5, leakingMTs);
        }

        leakingMTs.Should().ContainSingle("three leaks from the same MT count as one leaking publisher type, not three");
    }

    [Fact]
    public void AddToAccumulator_WithoutLeakingMTsArgument_DoesNotThrow()
    {
        var acc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), EventLeakAnalyzer.GroupAccumulator>();
        var leak = new EventLeakInfo { PublisherType = "App.Publisher", EventFieldName = "DataReady" };

        var act = () => EventLeakAnalyzer.AddToAccumulator(acc, leak, capacity: 5);

        act.Should().NotThrow("leakingMTs is optional so existing/other callers that don't care about the clean-vs-leaking count keep working");
    }

    // -----------------------------------------------------------------------
    // LooksLikeEventFieldName — allowBareUnderscorePrefix (P2-3, docs/analysis/phase1/eventleak-analyzer-audit.md)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("_onComplete")]
    [InlineData("_factory")]
    [InlineData("_selector")]
    [InlineData("_predicate")]
    public void LooksLikeEventFieldName_BareUnderscorePrefixDisallowed_RejectsOrdinaryCallbackFields(string fieldName)
    {
        // These are exactly the false-positive examples called out in the audit: private
        // delegate-typed fields that are callbacks/factories, not C# event backing fields.
        EventLeakAnalyzer.LooksLikeEventFieldName(fieldName, allowBareUnderscorePrefix: false)
            .Should().BeFalse($"'{fieldName}' has no event-specific name pattern and the type declares no real events");
    }

    [Theory]
    [InlineData("_onComplete")]
    [InlineData("_myEvent")]
    public void LooksLikeEventFieldName_BareUnderscorePrefixAllowed_AcceptsAnyUnderscoreField(string fieldName)
    {
        // Default (allowBareUnderscorePrefix: true) preserves the original, broader behavior —
        // used when the type is already known to declare at least one real event.
        EventLeakAnalyzer.LooksLikeEventFieldName(fieldName).Should().BeTrue();
    }

    [Theory]
    [InlineData("_myEventHandler")]   // "Handler"
    [InlineData("myEvent")]           // "Event"
    [InlineData("_onValueChanged")]   // "Changed"
    [InlineData("<MyEvent>k__BackingField")]
    public void LooksLikeEventFieldName_StrongNamePattern_AcceptedEvenWithoutBareUnderscorePrefix(string fieldName)
    {
        // Strong, event-specific substrings must still qualify a field regardless of whether the
        // bare "_" prefix fallback is allowed — tightening P2-3 must not regress these.
        EventLeakAnalyzer.LooksLikeEventFieldName(fieldName, allowBareUnderscorePrefix: false)
            .Should().BeTrue();
    }

    [Fact]
    public void LooksLikeEventFieldName_NullOrEmpty_AlwaysRejected()
    {
        EventLeakAnalyzer.LooksLikeEventFieldName(null).Should().BeFalse();
        EventLeakAnalyzer.LooksLikeEventFieldName(string.Empty).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // IsTimerEvent / IsPropertyChangedEvent (P3-3, docs/analysis/phase1/eventleak-analyzer-audit.md)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("System.Timers.Timer", "Elapsed")]
    [InlineData("System.Windows.Forms.Timer", "Tick")]
    [InlineData("System.Windows.Threading.DispatcherTimer", "Tick")]
    public void IsTimerEvent_KnownTimerTypeAndEvent_ReturnsTrue(string publisherType, string eventFieldName)
    {
        EventLeakAnalyzer.IsTimerEvent(publisherType, eventFieldName).Should().BeTrue();
    }

    [Theory]
    [InlineData("System.Timers.Timer", "Tick")]              // wrong event for this type
    [InlineData("System.Windows.Forms.Timer", "Elapsed")]    // wrong event for this type
    [InlineData("App.MyPublisher", "Elapsed")]                // not a timer type at all
    [InlineData("System.Threading.Timer", "Elapsed")]         // System.Threading.Timer has no event
    public void IsTimerEvent_MismatchedTypeOrEvent_ReturnsFalse(string publisherType, string eventFieldName)
    {
        EventLeakAnalyzer.IsTimerEvent(publisherType, eventFieldName).Should().BeFalse();
    }

    [Fact]
    public void IsPropertyChangedEvent_MatchesByNameOnly_RegardlessOfPublisherType()
    {
        // Any type can implement INotifyPropertyChanged — this is a name-only match.
        EventLeakAnalyzer.IsPropertyChangedEvent("PropertyChanged").Should().BeTrue();
    }

    [Fact]
    public void IsPropertyChangedEvent_OtherEventName_ReturnsFalse()
    {
        EventLeakAnalyzer.IsPropertyChangedEvent("PropertyChanging").Should().BeFalse();
        EventLeakAnalyzer.IsPropertyChangedEvent("Changed").Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Subscriber-count histogram (P3-4, docs/analysis/phase1/eventleak-analyzer-audit.md)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, "1")]
    [InlineData(2, "2")]
    [InlineData(3, "3-5")]
    [InlineData(5, "3-5")]
    [InlineData(6, "6-10")]
    [InlineData(10, "6-10")]
    [InlineData(11, "11-25")]
    [InlineData(25, "11-25")]
    [InlineData(26, "26-50")]
    [InlineData(50, "26-50")]
    [InlineData(51, "51-100")]
    [InlineData(100, "51-100")]
    [InlineData(101, "101+")]
    [InlineData(1_000_000, "101+")]
    public void GetSubscriberCountBucketIndex_ReturnsExpectedBucketLabel(int subscriberCount, string expectedLabel)
    {
        int idx = EventLeakAnalyzer.GetSubscriberCountBucketIndex(subscriberCount);

        EventLeakAnalyzer.SubscriberCountHistogramBuckets[idx].Label.Should().Be(expectedLabel);
    }

    [Fact]
    public void AddToAccumulator_IncrementsCorrectSubscriberCountBucket()
    {
        var acc = new Dictionary<(string PublisherType, string EventFieldName, bool IsStatic), EventLeakAnalyzer.GroupAccumulator>();
        var leak = new EventLeakInfo { PublisherType = "App.Publisher", EventFieldName = "DataReady", SubscriberCount = 7 };

        EventLeakAnalyzer.AddToAccumulator(acc, leak, capacity: 5);

        var group = acc[("App.Publisher", "DataReady", false)];
        int expectedIdx = EventLeakAnalyzer.GetSubscriberCountBucketIndex(7); // "6-10"
        group.SubscriberCountBuckets[expectedIdx].Should().Be(1);
        group.SubscriberCountBuckets.Sum().Should().Be(1, "exactly one leak was added, so exactly one bucket total");
    }

    [Fact]
    public void BuildSubscriberCountHistogram_FoldsAcrossAllGroups_InAscendingBucketOrder()
    {
        var bucketsA = new int[EventLeakAnalyzer.SubscriberCountHistogramBuckets.Length];
        bucketsA[0] = 3; // "1"
        var bucketsB = new int[EventLeakAnalyzer.SubscriberCountHistogramBuckets.Length];
        bucketsB[0] = 2; // "1"
        bucketsB[^1] = 5; // "101+"

        var groups = new List<EventGroupInfo>
        {
            new() { PublisherType = "A", EventFieldName = "E", SubscriberCountBuckets = bucketsA },
            new() { PublisherType = "B", EventFieldName = "E", SubscriberCountBuckets = bucketsB },
        };

        List<NameCountEntry> histogram = EventLeakAnalyzer.BuildSubscriberCountHistogram(groups);

        histogram.Should().HaveCount(EventLeakAnalyzer.SubscriberCountHistogramBuckets.Length);
        histogram[0].Name.Should().Be("1");
        histogram[0].Count.Should().Be(5, "3 (group A) + 2 (group B)");
        histogram[^1].Name.Should().Be("101+");
        histogram[^1].Count.Should().Be(5);
    }

    [Fact]
    public void BuildSubscriberCountHistogram_GroupWithNullBuckets_IsSkippedNotThrown()
    {
        var groups = new List<EventGroupInfo>
        {
            new() { PublisherType = "A", EventFieldName = "E", SubscriberCountBuckets = null },
        };

        var act = () => EventLeakAnalyzer.BuildSubscriberCountHistogram(groups);

        act.Should().NotThrow();
    }

    // -----------------------------------------------------------------------
    // ParseRootPublisher
    // -----------------------------------------------------------------------

    [Fact]
    public void ParseRootPublisher_TypicalQualifiedName_SplitsAtLastDot()
    {
        EventLeakAnalyzer.ParseRootPublisher("MyApp.Services.Publisher.OnDataChanged", out var publisherType, out var eventFieldName);

        publisherType.Should().Be("MyApp.Services.Publisher");
        eventFieldName.Should().Be("OnDataChanged");
    }

    [Fact]
    public void ParseRootPublisher_NoDot_ReturnsDefaults()
    {
        EventLeakAnalyzer.ParseRootPublisher("NoDotHere", out var publisherType, out var eventFieldName);

        publisherType.Should().Be("StaticRoot");
        eventFieldName.Should().Be("Unknown");
    }

    [Fact]
    public void ParseRootPublisher_TrailingDot_ReturnsDefaults()
    {
        EventLeakAnalyzer.ParseRootPublisher("MyApp.Publisher.", out var publisherType, out var eventFieldName);

        publisherType.Should().Be("StaticRoot");
        eventFieldName.Should().Be("Unknown");
    }

    [Fact]
    public void ParseRootPublisher_SingleDot_SplitsCorrectly()
    {
        EventLeakAnalyzer.ParseRootPublisher("A.B", out var publisherType, out var eventFieldName);

        publisherType.Should().Be("A");
        eventFieldName.Should().Be("B");
    }

    // -----------------------------------------------------------------------
    // BuildEventNameSet — Bug A fix
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
        var result = EventLeakAnalyzer.BuildEventNameSet(["add_Something"], []);
        result.Should().BeEmpty("no paired remove_ means nothing qualifies as a real event");
    }

    /// <summary>
    /// Validates the core invariant of Bug A: a type's own events merged with
    /// base-class events, so inherited backing fields are no longer rejected.
    /// </summary>
    [Fact]
    public void BuildEventNameSet_OwnPlusInheritedNames_ContainsBoth()
    {
        var ownAdd = new[] { "OwnEvent" };
        var ownRemove = new[] { "OwnEvent" };

        var baseAdd = new[] { "InheritedEvent" };
        var baseRemove = new[] { "InheritedEvent" };

        var allAdd = ownAdd.Concat(baseAdd).ToArray();
        var allRemove = ownRemove.Concat(baseRemove).ToArray();

        var names = EventLeakAnalyzer.BuildEventNameSet(allAdd, allRemove);

        names.Should().Contain("OwnEvent");
        names.Should().Contain("InheritedEvent");
    }

    // -----------------------------------------------------------------------
    // Phase 1 — Tier 1 retained bytes fold correctness (design §4.4, audit #3)
    // -----------------------------------------------------------------------

    [Fact]
    public void EstimateGroupRetainedBytes_FoldsOverAllSubscriberTypeCounts_NotJustCappedInstances()
    {
        // AllSubscriberTypeCounts reflects ALL instances in the group; Instances (the
        // capped top-N list) is left empty here to prove the estimate doesn't depend on it.
        var group = new EventGroupInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "Changed",
            TotalSubscribers = 30,
            AllSubscriberTypeCounts = new Dictionary<string, int>
            {
                ["App.SubscriberA"] = 20,
                ["App.SubscriberB"] = 10,
            }
        };
        var typeSizeMap = new Dictionary<string, ulong>
        {
            ["App.SubscriberA"] = 32,
            ["App.SubscriberB"] = 100,
        };

        ulong estimate = EventLeakAnalyzer.EstimateGroupRetainedBytes(group, typeSizeMap);

        estimate.Should().Be(20 * 32UL + 10 * 100UL);
    }

    [Fact]
    public void EstimateGroupRetainedBytes_UnknownSubscriberType_FallsBackTo64ByteEstimate()
    {
        var group = new EventGroupInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "Changed",
            AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.Unknown"] = 5 }
        };

        ulong estimate = EventLeakAnalyzer.EstimateGroupRetainedBytes(group, new Dictionary<string, ulong>());

        estimate.Should().Be(5 * 64UL);
    }

    [Fact]
    public void EstimateGroupRetainedBytes_NoSubscriberTypeCounts_ReturnsZero()
    {
        var group = new EventGroupInfo
        {
            PublisherType = "App.Publisher",
            EventFieldName = "Changed",
            AllSubscriberTypeCounts = new Dictionary<string, int>()
        };

        EventLeakAnalyzer.EstimateGroupRetainedBytes(group, new Dictionary<string, ulong>()).Should().Be(0);
    }

    [Fact]
    public void TotalEstimatedRetainedBytes_EqualsSumOfPerGroupEstimates()
    {
        var groups = new List<EventGroupInfo>
        {
            new()
            {
                PublisherType = "App.A",
                EventFieldName = "E1",
                AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.Sub1"] = 4 }
            },
            new()
            {
                PublisherType = "App.B",
                EventFieldName = "E2",
                AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.Sub2"] = 6 }
            },
        };
        var typeSizeMap = new Dictionary<string, ulong> { ["App.Sub1"] = 40, ["App.Sub2"] = 80 };

        ulong total = 0;
        foreach (var g in groups)
            total += EventLeakAnalyzer.EstimateGroupRetainedBytes(g, typeSizeMap);

        total.Should().Be(4 * 40UL + 6 * 80UL);
    }

    [Fact]
    public void BuildTopSubscriberTypesAcrossGroups_FoldsCountsAcrossGroups_SortedDescending()
    {
        var groups = new List<EventGroupInfo>
        {
            new()
            {
                PublisherType = "App.A",
                EventFieldName = "E1",
                AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.SubX"] = 10, ["App.SubY"] = 3 }
            },
            new()
            {
                PublisherType = "App.B",
                EventFieldName = "E2",
                AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.SubX"] = 15, ["App.SubZ"] = 1 }
            },
        };

        var result = EventLeakAnalyzer.BuildTopSubscriberTypesAcrossGroups(groups, topN: 20);

        result.Should().HaveCount(3);
        result[0].Should().Be(new NameCountEntry("App.SubX", 25));
        result[1].Should().Be(new NameCountEntry("App.SubY", 3));
        result[2].Should().Be(new NameCountEntry("App.SubZ", 1));
    }

    [Fact]
    public void BuildTopSubscriberTypesAcrossGroups_RespectsTopNBound()
    {
        var groups = new List<EventGroupInfo>
        {
            new()
            {
                PublisherType = "App.A",
                EventFieldName = "E1",
                AllSubscriberTypeCounts = new Dictionary<string, int> { ["App.SubX"] = 10, ["App.SubY"] = 3, ["App.SubZ"] = 1 }
            },
        };

        var result = EventLeakAnalyzer.BuildTopSubscriberTypesAcrossGroups(groups, topN: 2);

        result.Should().HaveCount(2);
        result.Should().Contain(new NameCountEntry("App.SubX", 10));
        result.Should().Contain(new NameCountEntry("App.SubY", 3));
    }

    [Fact]
    public void BuildTopHandlerMethodsAcrossGroups_FoldsByTypeAndMethod_AcrossGroups()
    {
        var groups = new List<EventGroupInfo>
        {
            new()
            {
                PublisherType = "App.A",
                EventFieldName = "E1",
                AllSubscriberMethodCounts = new Dictionary<(string Type, string? MethodName), int>
                {
                    [("App.Factory", "Wire")] = 8,
                    [("App.Other", "Handle")] = 2,
                }
            },
            new()
            {
                PublisherType = "App.B",
                EventFieldName = "E2",
                AllSubscriberMethodCounts = new Dictionary<(string Type, string? MethodName), int>
                {
                    [("App.Factory", "Wire")] = 5,
                }
            },
        };

        var result = EventLeakAnalyzer.BuildTopHandlerMethodsAcrossGroups(groups, topN: 20);

        result.Should().HaveCount(2);
        result[0].Should().Be(new NameCountEntry("App.Factory.Wire", 13));
        result[1].Should().Be(new NameCountEntry("App.Other.Handle", 2));
    }

    [Fact]
    public void BuildTopHandlerMethodsAcrossGroups_NullMethodName_RendersAsQuestionMark()
    {
        var groups = new List<EventGroupInfo>
        {
            new()
            {
                PublisherType = "App.A",
                EventFieldName = "E1",
                AllSubscriberMethodCounts = new Dictionary<(string Type, string? MethodName), int>
                {
                    [("App.Unresolved", null)] = 4,
                }
            },
        };

        var result = EventLeakAnalyzer.BuildTopHandlerMethodsAcrossGroups(groups, topN: 20);

        result.Should().ContainSingle().Which.Should().Be(new NameCountEntry("App.Unresolved.?", 4));
    }

    [Fact]
    public void BuildCorrelationViews_NoGroups_ReturnsEmpty()
    {
        var groups = new List<EventGroupInfo>();

        EventLeakAnalyzer.BuildTopSubscriberTypesAcrossGroups(groups, topN: 20).Should().BeEmpty();
        EventLeakAnalyzer.BuildTopHandlerMethodsAcrossGroups(groups, topN: 20).Should().BeEmpty();
    }
}
