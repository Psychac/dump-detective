namespace DumpDetective.Sdk.Observations;

/// <summary>
/// One raw quantitative fact. Must be raw — never a weighted composite or a value normalized
/// against a hand-picked constant; that judgment belongs in a synthesis rule, not here. See
/// docs/refactor/modularity/observation-and-correlation-model.md § 2a.
/// </summary>
public readonly record struct Measure(double Value, MeasureUnit Unit, MeasureSemantics Semantics);
