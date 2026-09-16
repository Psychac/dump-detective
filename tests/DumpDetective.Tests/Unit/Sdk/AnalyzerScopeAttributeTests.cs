using System.Reflection;

using DumpDetective.Cli.Execution;
using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Analysis;

using FluentAssertions;

using Xunit;

namespace DumpDetective.Tests.Unit.Sdk;

/// <summary>
/// Verifies <see cref="AnalyzerScopeAttribute"/>'s shape and its default —
/// docs/refactor/modularity/phase-4-session-orchestration.md migration step 5a. No real analyzer
/// declares <see cref="AnalyzerScope.PerSession"/> yet (nothing in the catalog is session-scoped
/// until Phase 5/7's correlation analyzers exist), so the real-catalog assertion here pins that
/// every analyzer either omits the attribute or explicitly declares
/// <see cref="AnalyzerScope.PerArtifact"/>.
/// </summary>
public sealed class AnalyzerScopeAttributeTests
{
    private static IReadOnlyList<IAnalyzer> RealCatalog() => new DefaultAnalyzerFactory().CreateAnalyzers();

    private static AnalyzerScope EffectiveScope(IAnalyzer analyzer) =>
        analyzer.GetType().GetCustomAttribute<AnalyzerScopeAttribute>()?.Scope ?? AnalyzerScope.PerArtifact;

    [Fact]
    public void UndeclaredAnalyzer_DefaultsToPerArtifact()
    {
        EffectiveScope(new UndeclaredScopeAnalyzer()).Should().Be(AnalyzerScope.PerArtifact);
    }

    [Fact]
    public void DeclaredAnalyzer_ReportsItsOwnScope()
    {
        EffectiveScope(new PerSessionScopeAnalyzer()).Should().Be(AnalyzerScope.PerSession);
    }

    [Fact]
    public void EveryCatalogAnalyzer_IsPerArtifactToday()
    {
        IReadOnlyList<IAnalyzer> analyzers = RealCatalog();

        analyzers.Should().HaveCount(36);
        analyzers.Should().OnlyContain(a => EffectiveScope(a) == AnalyzerScope.PerArtifact,
            "no analyzer is session-scoped yet — Phase 5/7's correlation analyzers are the first expected PerSession case");
    }

    private sealed class UndeclaredScopeAnalyzer : IAnalyzer
    {
        public string Name => nameof(UndeclaredScopeAnalyzer);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [AnalyzerScope(AnalyzerScope.PerSession)]
    private sealed class PerSessionScopeAnalyzer : IAnalyzer
    {
        public string Name => nameof(PerSessionScopeAnalyzer);
        public string Category => "Test";
        public ValueTask AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
