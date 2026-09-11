using DumpDetective.Analysis.SdkBridge;

namespace DumpDetective.Analysis.Analyzers.Types;

/// <summary>
/// Runs the retyped <see cref="ObjectShapeAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md's Tier-1 mop-up batch.
/// </summary>
public sealed class ObjectShapeAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ObjectShapeAnalyzer>
{
    public ObjectShapeAnalyzerLegacyAdapter() : base(new ObjectShapeAnalyzer())
    {
    }
}
