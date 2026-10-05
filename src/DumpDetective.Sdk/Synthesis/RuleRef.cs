namespace DumpDetective.Sdk.Synthesis;

/// <summary>The rule, and the version of it, that produced a claim. Stamped by the engine, never by the rule.</summary>
public readonly record struct RuleRef(string RuleId, int Version);
