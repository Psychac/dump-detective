namespace DumpDetective.Sdk.Artifacts;

/// <summary>
/// Identity of the process an artifact was captured from. This is how a session decides whether
/// two artifacts are even about the same thing — correlating a dump of process A with a trace of
/// process B is a category error the platform must refuse or loudly caveat, not silently ignore.
/// See docs/refactor/modularity/source-model.md § 2.
/// </summary>
public sealed record ProcessIdentity
{
    public required int ProcessId { get; init; }
    public required string ImageName { get; init; }
    public DateTime? StartTimeUtc { get; init; }
    public string? RuntimeVersion { get; init; }
}
