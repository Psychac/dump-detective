using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Artifacts;

namespace DumpDetective.TestPlugin;

/// <summary>
/// Minimal fixture analyzer used only to exercise <c>DropInPluginLoader</c> — proves an
/// out-of-tree, Sdk-only assembly can be discovered, isolated in its own
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and executed. Deliberately does not
/// touch <c>AnalyzerDomainResult</c>/<c>IProducesAnalyzerDomainResult</c> — those live in
/// <c>DumpDetective.Core</c>/<c>DumpDetective.Analysis</c>, which a genuine third-party plugin has
/// no access to; reporting a real finding through the Sdk-native <c>Observation</c> pipeline
/// (<see cref="AnalysisContext.Observations"/>) is deferred to whichever future phase actually
/// consumes drop-in plugin output (Phase 4's orchestrator does not exist yet).
/// </summary>
[AnalyzerModule(key: "test-plugin-sample", displayName: "Test Plugin Sample Analyzer", order: 9999, tags: ["test-plugin"])]
[OptionalCapability(CapabilityVocabulary.HeapObjects)]
public sealed class SampleDropInAnalyzer : IAnalyzer
{
    public static int RunCount;

    public string Name => "Test Plugin Sample Analyzer";

    public string Category => "Diagnostics";

    public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref RunCount);
        return ValueTask.CompletedTask;
    }
}
