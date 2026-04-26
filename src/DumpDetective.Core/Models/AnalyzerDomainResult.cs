namespace DumpDetective.Core.Models;

public abstract record AnalyzerDomainResult
{
    public string AnalyzerName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Metrics { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyCollection<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record GenericAnalyzerDomainResult : AnalyzerDomainResult;

/// <summary>A snapshot of a CLR type's object count and byte footprint on the heap.</summary>
public sealed record TypeSnapshot(string TypeName, int Count, ulong TotalBytes, ulong LohBytes);

/// <summary>Shared primitive: a name paired with an object count. Used across multiple domain results.</summary>
public sealed record NameCountEntry(string Name, int Count);

/// <summary>Shared primitive: a name paired with a byte size. Used across multiple domain results.</summary>
public sealed record NameBytesEntry(string Name, ulong Bytes);

// Analyzer-specific domain result types live in DumpDetective.Analysis.Models
// see: src/DumpDetective.Analysis/Models/AnalyzerDomainModels.cs
