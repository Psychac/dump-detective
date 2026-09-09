using DumpDetective.Platform.Storage.Container;
using DumpDetective.Sdk.Artifacts;
using DumpDetective.Sdk.Observations;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// A trace-fed analyzer emitting <see cref="Observation"/>s directly — Phase 6b's first analyzers
/// (docs/refactor/modularity/phase-6-trace-source.md § Phase 6b), predating both the capability-
/// declared plugin discovery of Phase 3 and the synthesis/matching engine of Phase 5, both deferred
/// under the § 8 minimum-viable path. This interface is deliberately minimal — no
/// <c>[RequiresCapability]</c> attribute, no registry lookup — because nothing in this codebase
/// resolves those yet; it exists only so <c>TraceAnalysisRunner</c> can run more than one analyzer
/// without hard-coding each one by name.
/// </summary>
internal interface ITraceAnalyzer
{
    string Name { get; }

    /// <summary>
    /// Sections this analyzer reads — lets <c>TraceAnalysisRunner</c> ask
    /// <c>TraceIndexBuilder.Build</c> for only what's actually consumed, instead of always building
    /// every section (see <c>TraceIndexBuilder.Build</c>'s <c>sections</c> parameter for why that
    /// matters: skipping an unneeded section is a real wall-clock saving, not a micro-optimization).
    /// </summary>
    IReadOnlySet<CacheSectionId> RequiredSections { get; }

    /// <summary>
    /// Reads whatever sections it needs from <paramref name="container"/> and returns the
    /// observations it produced. Must not throw for a missing/empty section — that's "no findings",
    /// not a failure; see call sites in <c>TraceAnalysisRunner</c>.
    /// </summary>
    IReadOnlyList<Observation> Analyze(CacheContainerReader container, ArtifactId artifactId);
}
