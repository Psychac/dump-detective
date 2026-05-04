namespace DumpDetective.Core.Options;

public sealed class WeakReferenceAnalysisOptions
{
    public int HandleScanCap { get; init; } = 50_000;
    public int TopTypeLimit { get; init; } = 15;
}
