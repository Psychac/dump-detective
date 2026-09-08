using DumpDetective.Analysis.Algorithms;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Algorithms;

public sealed class SpaceSavingCounterTests
{
    [Fact]
    public void Offer_BelowCapacity_TracksExactCountsWithZeroError()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 10);

        counter.Offer(1);
        counter.Offer(1);
        counter.Offer(2);

        counter.TryGetCount(1, out int count1, out int error1).Should().BeTrue();
        count1.Should().Be(2);
        error1.Should().Be(0);

        counter.TryGetCount(2, out int count2, out int error2).Should().BeTrue();
        count2.Should().Be(1);
        error2.Should().Be(0);

        counter.TrackedCount.Should().Be(2);
    }

    [Fact]
    public void Offer_UnseenKeyBelowCapacity_ReturnsFalse()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 10);

        counter.Offer(1).Should().BeFalse();
        counter.Offer(1).Should().BeFalse();
    }

    [Fact]
    public void Offer_UnseenKeyAtCapacity_ReturnsTrueAndEvictsMinimum()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 2);

        counter.Offer(1); // count 1
        counter.Offer(2); // count 1
        counter.Offer(2); // count 2 — key 1 is now the unique minimum at count 1

        bool wasApproximated = counter.Offer(3);

        wasApproximated.Should().BeTrue();
        counter.TrackedCount.Should().Be(2);
        counter.TryGetCount(1, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Offer_AtCapacity_ReplacementCountIsAtLeastEvictedCountPlusIncrement()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 1);

        counter.Offer(1, increment: 5);
        counter.Offer(2); // evicts key 1 (count 5)

        counter.TryGetCount(2, out int count, out int error).Should().BeTrue();
        count.Should().Be(6); // 5 (evicted count) + 1
        error.Should().Be(5);
    }

    [Fact]
    public void Offer_HighFrequencyKeyArrivingLate_SurvivesDespiteCapacityFull()
    {
        // Regression coverage for the admission-order bias this type replaces
        // (docs/analysis/phase1/dominator-analyzer-audit.md Area 6 item 3): a plain
        // fixed-capacity dictionary would have permanently excluded key 999 here because
        // it arrives only after the table is already full of single-occurrence keys.
        var counter = new SpaceSavingCounter<int>(capacity: 4);

        for (int i = 0; i < 4; i++)
            counter.Offer(i); // fills capacity with 4 keys at count 1 each

        for (int i = 0; i < 1000; i++)
            counter.Offer(999); // arrives late, but is by far the highest true frequency

        counter.TryGetCount(999, out int count, out _).Should().BeTrue();
        count.Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public void Offer_NeverUnderCountsATrackedKey()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 3);
        var trueOccurrences = new Dictionary<int, int>();
        var random = new Random(Seed: 42);

        for (int i = 0; i < 5000; i++)
        {
            int key = random.Next(0, 20);
            counter.Offer(key);
            trueOccurrences[key] = trueOccurrences.GetValueOrDefault(key) + 1;
        }

        foreach ((int key, int reportedCount, int error) in counter.Entries)
        {
            reportedCount.Should().BeGreaterThanOrEqualTo(trueOccurrences[key]);
            (reportedCount - error).Should().BeLessThanOrEqualTo(trueOccurrences[key]);
        }
    }

    [Fact]
    public void TrackedCount_NeverExceedsCapacity()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 5);

        for (int i = 0; i < 10_000; i++)
            counter.Offer(i);

        counter.TrackedCount.Should().Be(5);
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Action act = () => _ = new SpaceSavingCounter<int>(capacity: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Offer_NonPositiveIncrement_Throws()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 5);
        Action act = () => counter.Offer(1, increment: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryGetCount_UnknownKey_ReturnsFalse()
    {
        var counter = new SpaceSavingCounter<int>(capacity: 5);
        counter.TryGetCount(123, out int count, out int error).Should().BeFalse();
        count.Should().Be(0);
        error.Should().Be(0);
    }
}
