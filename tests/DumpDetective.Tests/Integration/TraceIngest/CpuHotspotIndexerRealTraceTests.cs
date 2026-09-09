using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Identity;
using DumpDetective.Sdk.Observations;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Integration.TraceIngest;

/// <summary>
/// End-to-end verification of Phase 6b's CPU hotspot slice (trace.cpu-samples ingest +
/// CpuHotspotAnalyzer) against a real capture — docs/refactor/modularity/phase-6-trace-source.md
/// § Phase 6b.
/// </summary>
public sealed class CpuHotspotIndexerRealTraceTests : IDisposable
{
    private static readonly string TracePath = Environment.GetEnvironmentVariable("DD_BENCHMARK_ETL")
        ?? @"D:\Dumps\08-05\etls\HighCPU_11.etl";

    private readonly string _containerPath;

    public CpuHotspotIndexerRealTraceTests()
    {
        _containerPath = Path.Combine(Path.GetTempPath(), $"trace-cpu-hotspot-test-{Guid.NewGuid():N}.bin");
    }

    public void Dispose()
    {
        if (File.Exists(_containerPath))
            File.Delete(_containerPath);
    }

    [RealTraceFact]
    public void Build_RealEtlCapture_ProducesReadableCpuSamplesSection()
    {
        File.Exists(TracePath).Should().BeTrue($"expected real .etl at {TracePath} or a DD_BENCHMARK_ETL override.");

        TraceIndexBuilder.Build(TracePath, _containerPath, targetProcessId: null);

        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();
        reader.Should().NotBeNull();
        reader!.ContainsSection(CacheSectionId.TraceCpuSamples).Should().BeTrue();
        reader.TryGetSectionInfo(CacheSectionId.TraceCpuSamples, out CacheTocEntry entry).Should().BeTrue();
        entry.RecordCount.Should().BeGreaterThan(0);
    }

    [RealTraceFact]
    public void Analyze_RealEtlCapture_ResolvesLeafSamplesToRealManagedMethods()
    {
        File.Exists(TracePath).Should().BeTrue($"expected real .etl at {TracePath} or a DD_BENCHMARK_ETL override.");

        TraceIndexBuilder.Build(TracePath, _containerPath, targetProcessId: null);
        CacheContainerReader.TryOpen(_containerPath, out CacheContainerReader? reader).Should().BeTrue();

        var analyzer = new CpuHotspotAnalyzer();
        IReadOnlyList<Observation> observations = analyzer.Analyze(reader!, new DumpDetective.Sdk.Artifacts.ArtifactId(TracePath));

        observations.Should().NotBeEmpty("a real HighCPU capture should resolve at least some leaf samples to a managed method");
        observations.Should().OnlyContain(o => o.ObservationType == "cpu.sample-attribution");
        observations.Should().OnlyContain(o => o.Subjects.Count == 1 && o.Subjects[0] is MethodRef);
        observations.Should().OnlyContain(o => o.Measures["cpu.sample-count"].Value > 0);
        observations.Should().OnlyContain(o => !string.IsNullOrEmpty(((MethodRef)o.Subjects[0]).Name));

        // Not asserting JoinKey uniqueness here: real capture data shows it legitimately collides
        // for Low/Medium-fidelity compiler-generated methods (distinct closures/state machines the
        // canonicalizer intentionally strips down to an ambiguous shared name — the exact,
        // already-documented "safe to join only within one build" limitation
        // docs/refactor/modularity/source-model.md § 4 describes for lambdas/closures). Dedup by
        // the trace-side MethodId is what CpuHotspotAnalyzer's aggregation actually guarantees, and
        // that's covered precisely by CpuHotspotAnalyzerTests' synthetic boundary cases instead.
    }
}
