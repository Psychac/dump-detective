using DumpDetective.Analysis.Analyzers;
using DumpDetective.Analysis.Models;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Analysis;

public sealed class CrashAnalyzerCrashBucketTests
{
    [Fact]
    public void BuildCrashBuckets_GroupsSameTypeAndTopUserFrame_IntoOneBucket()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["SqlException"] =
                [
                    WithStack(0x1000, "System.Data.SqlClient.SqlCommand.ExecuteReader()", "MyApp.Data.OrderRepository.GetOrder()"),
                    WithStack(0x1100, "System.Data.SqlClient.SqlCommand.ExecuteReader()", "MyApp.Data.OrderRepository.GetOrder()"),
                ]
            }
        };

        IReadOnlyList<CrashBucket> buckets = CrashAnalyzer.BuildCrashBuckets(analysis);

        buckets.Should().ContainSingle();
        buckets[0].ExceptionType.Should().Be("SqlException");
        buckets[0].TopUserFrame.Should().Be("MyApp.Data.OrderRepository.GetOrder()");
        buckets[0].InstanceCount.Should().Be(2);
    }

    [Fact]
    public void BuildCrashBuckets_DifferentTopUserFrames_ProduceSeparateBuckets_SortedByInstanceCount()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["NullReferenceException"] =
                [
                    WithStack(0x1000, "MyApp.SiteA.Handler.Process()"),
                    WithStack(0x1100, "MyApp.SiteB.Handler.Process()"),
                    WithStack(0x1200, "MyApp.SiteB.Handler.Process()"),
                ]
            }
        };

        IReadOnlyList<CrashBucket> buckets = CrashAnalyzer.BuildCrashBuckets(analysis);

        buckets.Should().HaveCount(2);
        buckets[0].TopUserFrame.Should().Be("MyApp.SiteB.Handler.Process()");
        buckets[0].InstanceCount.Should().Be(2);
        buckets[1].TopUserFrame.Should().Be("MyApp.SiteA.Handler.Process()");
        buckets[1].InstanceCount.Should().Be(1);
    }

    [Fact]
    public void BuildCrashBuckets_AllFramesAreFramework_FallsBackToTopFrame()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["IOException"] = [WithStack(0x1000, "System.IO.FileStream.Read()", "Microsoft.Extensions.FileProviders.PhysicalFileInfo.CreateReadStream()")]
            }
        };

        IReadOnlyList<CrashBucket> buckets = CrashAnalyzer.BuildCrashBuckets(analysis);

        buckets.Should().ContainSingle();
        buckets[0].TopUserFrame.Should().Be("System.IO.FileStream.Read()");
    }

    [Fact]
    public void BuildCrashBuckets_NoStackTrace_UsesSentinel()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["Exception"] = [new ExceptionInstance { Address = 0x1000 }]
            }
        };

        IReadOnlyList<CrashBucket> buckets = CrashAnalyzer.BuildCrashBuckets(analysis);

        buckets.Should().ContainSingle();
        buckets[0].TopUserFrame.Should().Be("(no stack trace)");
    }

    [Fact]
    public void BuildCrashBuckets_TracksActiveInstanceCountSeparately()
    {
        var analysis = new ExceptionAnalysis
        {
            ExceptionsByType = new()
            {
                ["TimeoutException"] =
                [
                    WithStack(0x1000, "MyApp.Worker.Run()"),
                    Active(0x1100, threadId: 3, "MyApp.Worker.Run()"),
                ]
            }
        };

        IReadOnlyList<CrashBucket> buckets = CrashAnalyzer.BuildCrashBuckets(analysis);

        buckets.Should().ContainSingle();
        buckets[0].InstanceCount.Should().Be(2);
        buckets[0].ActiveInstanceCount.Should().Be(1);
    }

    private static ExceptionInstance WithStack(ulong address, params string[] frames) => new()
    {
        Address = address,
        OriginalStackTrace = [.. frames]
    };

    private static ExceptionInstance Active(ulong address, uint threadId, params string[] frames) => new()
    {
        Address = address,
        ThreadId = threadId,
        OriginalStackTrace = [.. frames]
    };
}
