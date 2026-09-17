using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.Services;

internal interface ISynthesisRuleFactory
{
    IReadOnlyList<ISynthesisRule> CreateRules();
}
