using System.Linq;

using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.TraceIngest;

/// <summary>
/// Fast, deterministic characterization of <see cref="TraceCpuSampleQuery"/>'s address-range
/// resolution against synthetic <c>trace.methods</c>/<c>trace.cpu-samples</c> sections — the real-
/// trace tests (<c>CpuHotspotIndexerRealTraceTests</c>) prove this works against real data but can't
/// cheaply exercise exact boundary cases (a sample landing on a method's first byte, its last valid
/// byte, one past the end, or in the gap between two methods) the way a synthetic container can.
/// Moved here from <c>CpuHotspotAnalyzerTests</c> when the address-range resolution itself moved to
/// this type in docs/refactor/modularity/phase-4-session-orchestration.md migration step 5b.
/// </summary>
public sealed class TraceCpuSampleQueryTests : IDisposable
{
    private const ulong MethodAStart = 0x1000;
    private const int MethodASize = 0x10; // valid range: [0x1000, 0x1010)
    private const ulong MethodBStart = 0x2000;
    private const int MethodBSize = 0x20; // valid range: [0x2000, 0x2020)

    private readonly string _containerPath;

    public TraceCpuSampleQueryTests()
    {
        _containerPath = Path.Combine(Path.GetTempPath(), $"trace-cpu-sample-query-unit-test-{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_containerPath))
            File.Delete(_containerPath);
    }

    [Fact]
    public void EnumerateSampleAttributions_SamplesAtBoundaries_ResolveExactlyTheContainingMethod()
    {
        BuildContainer(samples:
        [
            (MethodAStart, 1L), // exact start of A -> resolves to A
            (MethodAStart + MethodASize - 1, 2L), // last valid byte of A -> resolves to A
            (MethodAStart + MethodASize, 3L), // one past the end of A -> unresolved (no method owns it)
            (MethodBStart - 1, 4L), // one before B, in the gap -> unresolved
            (MethodBStart, 5L), // exact start of B -> resolves to B
            (MethodBStart + 5, 6L), // mid-range of B -> resolves to B
        ]);

        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();
        var query = new TraceCpuSampleQuery(reader!);

        CpuSampleAttribution[] attributions = query.EnumerateSampleAttributions().ToArray();

        attributions.Should().HaveCount(2, "only MethodA and MethodB should have resolved samples");

        CpuSampleAttribution methodA = attributions.Single(a => a.Method.Name == "MethodA");
        methodA.SampleCount.Should().Be(2, "two samples fall inside MethodA's range");

        CpuSampleAttribution methodB = attributions.Single(a => a.Method.Name == "MethodB");
        methodB.SampleCount.Should().Be(2, "two samples fall inside MethodB's range");
    }

    [Fact]
    public void EnumerateSampleAttributions_NoCpuSamplesSection_ReturnsEmpty()
    {
        using (var containerWriter = new CacheContainerWriter(_containerPath))
        {
            containerWriter.BeginSection(CacheSectionId.TraceMethods);
            using (var writer = new TraceMethodIndexWriter(containerWriter.Stream))
            {
                writer.Add(1, 0, MethodAStart, MethodASize, 0, MatchFidelity.Exact, "Some.Type", "MethodA", "()");
                containerWriter.EndSection(writer.Flush());
            }

            containerWriter.Finish();
        }

        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();
        var query = new TraceCpuSampleQuery(reader!);

        query.EnumerateSampleAttributions().Should().BeEmpty();
    }

    private void BuildContainer(IReadOnlyList<(ulong InstructionPointer, long TimestampTicks)> samples)
    {
        using var containerWriter = new CacheContainerWriter(_containerPath);

        containerWriter.BeginSection(CacheSectionId.TraceMethods);
        using (var methodWriter = new TraceMethodIndexWriter(containerWriter.Stream))
        {
            methodWriter.Add(1, 0, MethodAStart, MethodASize, 0, MatchFidelity.Exact, "Some.Type", "MethodA", "()");
            methodWriter.Add(2, 0, MethodBStart, MethodBSize, 0, MatchFidelity.Exact, "Some.Type", "MethodB", "()");
            containerWriter.EndSection(methodWriter.Flush());
        }

        containerWriter.BeginSection(CacheSectionId.TraceCpuSamples);
        using (var sampleWriter = new CpuSampleIndexWriter(containerWriter.Stream))
        {
            foreach ((ulong ip, long ticks) in samples)
                sampleWriter.Add(ticks, processId: 1, threadId: 1, instructionPointer: ip);
            containerWriter.EndSection(sampleWriter.Flush());
        }

        containerWriter.Finish();
    }
}
