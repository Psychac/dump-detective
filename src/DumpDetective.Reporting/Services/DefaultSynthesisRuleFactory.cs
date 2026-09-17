using DumpDetective.Sdk.Synthesis;

using Microsoft.Extensions.DependencyInjection;

namespace DumpDetective.Reporting.Services;

/// <summary>
/// Mirrors <see cref="DefaultAnalyzerFactory"/> exactly — same <c>ActivatorUtilities</c>
/// construction, same "one factory reads one catalog" shape — rather than inventing a second
/// registration story for rules.
/// </summary>
internal sealed class DefaultSynthesisRuleFactory : ISynthesisRuleFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISynthesisRuleCatalog _catalog;

    public DefaultSynthesisRuleFactory(IServiceProvider serviceProvider, ISynthesisRuleCatalog catalog)
    {
        _serviceProvider = serviceProvider;
        _catalog = catalog;
    }

    public IReadOnlyList<ISynthesisRule> CreateRules()
    {
        var rules = new List<ISynthesisRule>(_catalog.RuleTypes.Count);

        foreach (Type ruleType in _catalog.RuleTypes)
        {
            rules.Add((ISynthesisRule)ActivatorUtilities.CreateInstance(_serviceProvider, ruleType));
        }

        return rules;
    }
}
