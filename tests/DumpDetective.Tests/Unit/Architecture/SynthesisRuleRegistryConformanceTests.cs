using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Synthesis;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace DumpDetective.Tests.Unit.Architecture;

/// <summary>
/// Guards <see cref="ISynthesisRule.RuleId"/> uniqueness across every rule the production catalog
/// registers. Trivially green today — <see cref="DefaultSynthesisRuleCatalog"/> is empty, since no
/// rule exists yet (see docs/refactor/modularity/phase-5-observations-synthesis.md's "Before step
/// 2" prerequisites) — but it's the proportionate response to the real collision risk named there:
/// rules will be authored across the same plugin packages analyzers already are, by the same
/// people over time, so a duplicate <c>RuleId</c> is a real mistake to catch, not a hypothetical
/// one. A full versioned JSON registry (the observation-type/capability pattern) isn't warranted —
/// <c>RuleId</c> has exactly one reader, the synthesis engine itself, not the many independent
/// consumers that justified that registry.
/// </summary>
public sealed class SynthesisRuleRegistryConformanceTests
{
    [Fact]
    public void SynthesisRuleCatalog_ShouldHaveUniqueRuleIds()
    {
        ServiceCollection services = new();
        services.AddSingleton<ISynthesisRuleCatalog, DefaultSynthesisRuleCatalog>();
        services.AddSingleton<ISynthesisRuleFactory, DefaultSynthesisRuleFactory>();
        using ServiceProvider provider = services.BuildServiceProvider();

        ISynthesisRuleFactory factory = provider.GetRequiredService<ISynthesisRuleFactory>();
        IReadOnlyList<ISynthesisRule> rules = factory.CreateRules();

        rules.Select(r => r.RuleId).Should().OnlyHaveUniqueItems(
            "two synthesis rules registered under the same RuleId would collide silently in " +
            "Finding.DerivedFrom lineage and in any future trend/dedup identity built on RuleId");
    }
}
