using DumpDetective.Sdk.Observations;
using DumpDetective.Sdk.Synthesis;

namespace DumpDetective.Reporting.SynthesisRules;

/// <summary>Terse constructors for the measures and budgets a rule states in its assertion.</summary>
internal static class ClaimParts
{
    public static Measure Count(double value) => new(value, MeasureUnit.Count, MeasureSemantics.Count);

    public static Measure Bytes(double value) => new(value, MeasureUnit.Bytes, MeasureSemantics.Absolute);

    public static Measure Percent(double value) => new(value, MeasureUnit.Percent, MeasureSemantics.Ratio);

    /// <summary>The rule's own threshold as the reference an <see cref="AssertionPredicate.Exceeds"/> compares against.</summary>
    public static Reference Budget(Measure threshold, string basis) => new() { Value = threshold, Basis = basis };
}
