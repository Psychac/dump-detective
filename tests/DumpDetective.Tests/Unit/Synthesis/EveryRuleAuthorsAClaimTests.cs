using DumpDetective.Reporting.Services;
using DumpDetective.Sdk.Synthesis;
using DumpDetective.Tests.Golden;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace DumpDetective.Tests.Unit.Synthesis;

/// <summary>
/// M1 step 4's completeness guard: a rule added to the catalog without authoring a structured claim, or without
/// a golden pinning it, fails here instead of silently shipping as a prose-only finding.
/// </summary>
public sealed class EveryRuleAuthorsAClaimTests
{
    private static IReadOnlyList<ISynthesisRule> CatalogRules()
    {
        IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        return new DefaultSynthesisRuleCatalog().RuleTypes
            .Select(type => (ISynthesisRule)ActivatorUtilities.CreateInstance(services, type))
            .ToArray();
    }

    [Fact]
    public void EveryCatalogRule_HasAGolden_RecordingItsCurrentVersion()
    {
        string directory = RuleShapeGolden.FindGoldenDirectory();
        var problems = new List<string>();

        foreach (ISynthesisRule rule in CatalogRules())
        {
            string path = Path.Combine(directory, $"{rule.RuleId}.golden");
            if (!File.Exists(path))
            {
                problems.Add($"{rule.RuleId}: no golden");
                continue;
            }

            string versionLine = File.ReadLines(path).Skip(1).First().Trim();
            if (versionLine != $"version: {rule.Version}")
                problems.Add($"{rule.RuleId}: golden says '{versionLine}', rule is version {rule.Version}");
        }

        problems.Should().BeEmpty("every rule must author a structured claim pinned by a golden at its current version");
    }

    [Fact]
    public void RuleIds_AreUnique_SoNoGoldenIsSharedByTwoRules()
    {
        CatalogRules().Select(rule => rule.RuleId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Catalog_ListsEveryRuleClassInTheAssembly()
    {
        var catalogued = new DefaultSynthesisRuleCatalog().RuleTypes.ToHashSet();
        Type[] declared = typeof(DefaultSynthesisRuleCatalog).Assembly.GetTypes()
            .Where(type => typeof(ISynthesisRule).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })
            .ToArray();

        declared.Where(type => !catalogued.Contains(type)).Select(type => type.Name).Should().BeEmpty();
        catalogued.Count.Should().Be(declared.Length);
    }
}
