using DumpDetective.Cli.Execution;
using DumpDetective.Orchestration.Discovery;
using DumpDetective.Orchestration.Session;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sources.NetTrace;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Orchestration;

/// <summary>
/// Closes Phase 3's exit criterion left half-open by migration step 3
/// (docs/refactor/modularity/phase-4-session-orchestration.md) — "the capability-satisfaction
/// report correctly lists <c>Plugins.Cpu</c>'s analyzers as skipped in a dump-only session." Step 3
/// could only partially close it: `CpuHotspotAnalyzer` wasn't in the resolvable catalog at all yet
/// (it was still on the old <c>ITraceAnalyzer</c> interface), so there was nothing to show as
/// skipped. Step 5b's retyping onto <see cref="IAnalyzer"/> made a real unified catalog possible;
/// this verifies resolving it against each session shape produces the expected skip/satisfy split.
/// </summary>
public sealed class UnifiedCapabilityResolutionTests
{
    private static IReadOnlyList<IAnalyzer> UnifiedCatalog() =>
        [.. new DefaultAnalyzerFactory().CreateAnalyzers(), new CpuHotspotAnalyzer(), new GcPauseAnalyzer(), new ContentionAnalyzer()];

    [Fact]
    public void DumpOnlySession_SkipsExactlyTheThreeTraceAnalyzers()
    {
        IReadOnlyList<IAnalyzer> unified = UnifiedCatalog();
        unified.Should().HaveCount(39);

        ArtifactDescriptor dumpArtifact = SingleDumpArtifactFactory.Create("C:/fake/dump.dmp");
        AnalysisSession session = SessionBuilder.Build([dumpArtifact]);

        CapabilityResolution resolution = CapabilityResolver.Resolve(unified, session.AvailableCapabilities);

        resolution.Satisfied.Should().HaveCount(36, "the 36 dump analyzers all still resolve against a dump-only session");
        resolution.Unsatisfied.Should().HaveCount(3, "the 3 trace analyzers require trace.* capabilities a dump-only session lacks");
        resolution.Unsatisfied.Select(u => u.Analyzer.Name).Should().BeEquivalentTo(
            ["CpuHotspotAnalyzer", "GcPauseAnalyzer", "ContentionAnalyzer"]);
    }

    [Fact]
    public void TraceOnlySession_SkipsExactlyTheThirtySixDumpAnalyzers()
    {
        IReadOnlyList<IAnalyzer> unified = UnifiedCatalog();

        ArtifactDescriptor traceArtifact = TraceArtifactFactory.Create("C:/fake/trace.nettrace");
        AnalysisSession session = SessionBuilder.Build([traceArtifact]);

        CapabilityResolution resolution = CapabilityResolver.Resolve(unified, session.AvailableCapabilities);

        resolution.Satisfied.Should().HaveCount(3, "only the 3 trace analyzers resolve against a trace-only session");
        resolution.Satisfied.Select(a => a.Name).Should().BeEquivalentTo(
            ["CpuHotspotAnalyzer", "GcPauseAnalyzer", "ContentionAnalyzer"]);
        resolution.Unsatisfied.Should().HaveCount(36, "every dump analyzer requires a heap.*/runtime.*/dump.* capability a trace-only session lacks");
    }

    [Fact]
    public void CombinedDumpAndTraceSession_SkipsNoAnalyzer()
    {
        IReadOnlyList<IAnalyzer> unified = UnifiedCatalog();

        ArtifactDescriptor dumpArtifact = SingleDumpArtifactFactory.Create("C:/fake/dump.dmp");
        ArtifactDescriptor traceArtifact = TraceArtifactFactory.Create("C:/fake/trace.nettrace");
        AnalysisSession session = SessionBuilder.Build([dumpArtifact, traceArtifact]);

        CapabilityResolution resolution = CapabilityResolver.Resolve(unified, session.AvailableCapabilities);

        resolution.Satisfied.Should().HaveCount(39, "a combined session provides every heap.*/runtime.*/dump.*/trace.* capability the unified catalog needs");
        resolution.Unsatisfied.Should().BeEmpty();
    }
}
