namespace DumpDetective.Analysis.Models;

/// <summary>
/// Shared "why is this alive / why does this matter" shape for leak-adjacent analyzers.
/// Consumed by the ranking engine and confidence scoring; produced only for a small
/// top-K set of items per analyzer, never for every candidate before filtering.
/// </summary>
internal sealed record Evidence(
    ulong EstimatedRetainedBytes,
    string? SampleRootPath,
    bool RootPathSearchTruncated,
    IReadOnlyList<EvidenceSignal> ContributingSignals);

internal readonly record struct EvidenceSignal(string Name, string Description, double? Value = null);
