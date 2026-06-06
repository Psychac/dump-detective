namespace DumpDetective.Core.Models;

public abstract record AnalyzerDomainResult
{
    public string AnalyzerName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ReportArtifact> Artifacts { get; init; } = [];
}

/// <summary>
/// Used only for tests and benchmarks.
/// </summary>
public sealed record GenericAnalyzerDomainResult : AnalyzerDomainResult;

/// <summary>A snapshot of a CLR type's object count and byte footprint on the heap.</summary>
/// <param name="AverageSize">Average shallow object size in bytes (TotalBytes / Count). Zero when Count is zero.</param>
/// <param name="EstimatedRetainedBytes">Approximate retained bytes including the reference sub-graph. Zero until populated by a retention/dominator analyzer.</param>
public sealed record TypeSnapshot(
    string TypeName,
    int Count,
    ulong TotalBytes,
    ulong LohBytes,
    ulong AverageSize = 0,
    ulong EstimatedRetainedBytes = 0,
    ulong SampleAddress = 0,
    string? ModuleName = null);

/// <summary>Shared primitive: a name paired with an object count. Used across multiple domain results.</summary>
public sealed record NameCountEntry(string Name, int Count);

/// <summary>Shared primitive: a name paired with a byte size. Used across multiple domain results.</summary>
public sealed record NameBytesEntry(string Name, ulong Bytes);

// Analyzer-specific domain result types live in DumpDetective.Analysis.Models
// see: src/DumpDetective.Analysis/Models/AnalyzerDomainModels.cs
