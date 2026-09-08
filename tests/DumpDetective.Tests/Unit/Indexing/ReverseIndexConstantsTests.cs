using DumpDetective.Analysis.Indexing.ReverseIndex;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Indexing;

public class ReverseIndexConstantsTests
{
    [Fact]
    public void ChildBucketHash_DeterministicForSameChild()
    {
        const ulong child = 0x00007fff_12345678;
        const int bucketCount = 4;

        uint hash1 = ReverseIndexConstants.ChildBucketHash(child, bucketCount);
        uint hash2 = ReverseIndexConstants.ChildBucketHash(child, bucketCount);
        uint hash3 = ReverseIndexConstants.ChildBucketHash(child, bucketCount);

        hash1.Should().Be(hash2);
        hash2.Should().Be(hash3);
    }

    [Fact]
    public void ChildBucketHash_ReturnsBucketInRange()
    {
        const int bucketCount = 10;
        var children = new ulong[] { 0x1000, 0x2000, 0x3000, 0x4000, 0x5000 };

        foreach (var child in children)
        {
            uint bucket = ReverseIndexConstants.ChildBucketHash(child, bucketCount);
            bucket.Should().BeLessThan(bucketCount);
        }
    }

    [Fact]
    public void ChildBucketHash_DistributesUniformly()
    {
        const int bucketCount = 8;
        const int sampleSize = 10000;

        var distribution = new int[bucketCount];

        for (ulong i = 0; i < sampleSize; i++)
        {
            ulong child = (i * 0x100000001b3) ^ 0xcbf29ce484222325;
            uint bucket = ReverseIndexConstants.ChildBucketHash(child, bucketCount);
            distribution[bucket]++;
        }

        var average = sampleSize / bucketCount;
        var tolerance = average * 0.15; // Allow ±15% variance

        foreach (var count in distribution)
        {
            count.Should().BeCloseTo(average, (uint)tolerance);
        }
    }

    [Fact]
    public void CalculateBucketCount_SmallDump()
    {
        long smallDump = 3270L * 1024 * 1024; // 3.27 GB (from validation)
        int bucketCount = ReverseIndexConstants.CalculateBucketCount(smallDump);

        bucketCount.Should().Be(7); // 3270 / 500 = 6.54 → 7
    }

    [Fact]
    public void CalculateBucketCount_LargeDump()
    {
        long largeDump = 25630L * 1024 * 1024; // 25.63 GB (from validation)
        int bucketCount = ReverseIndexConstants.CalculateBucketCount(largeDump);

        bucketCount.Should().Be(52); // 25630 / 500 = 51.26 → 52
    }

    [Fact]
    public void CalculateBucketCount_HugeDump()
    {
        long hugeDump = 150L * 1024 * 1024 * 1024; // 150 GB
        int bucketCount = ReverseIndexConstants.CalculateBucketCount(hugeDump);

        bucketCount.Should().Be(308); // 150*1024 / 500 = 307.2 → 308
    }

    [Fact]
    public void CalculateBucketCount_MinimumOne()
    {
        long tinyDump = 100 * 1024 * 1024; // 100 MB
        int bucketCount = ReverseIndexConstants.CalculateBucketCount(tinyDump);

        bucketCount.Should().Be(1);
    }
}
