namespace DumpDetective.Core.Options;

public sealed class JitAnalysisOptions
{
    // 64 KB — arbitrary round number, no theoretical basis; revisit with field data.
    public uint LargeMethodThresholdBytes { get; init; } = 64 * 1024;
}
