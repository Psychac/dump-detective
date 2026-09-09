namespace DumpDetective.Sdk.Identity;

/// <summary>
/// An OS thread. <see cref="JoinKey"/> is the OS thread id — the one identifier both a dump and a
/// trace agree on, since managed thread ids can be reused within a process lifetime while OS
/// thread ids (combined with process identity, checked separately at the join site) are stable for
/// the process's lifetime. See docs/refactor/modularity/source-model.md § 4.
/// </summary>
public sealed record ThreadRef : EntityRef
{
    public override EntityKind Kind => EntityKind.Thread;

    public required uint OsThreadId { get; init; }

    public int? ManagedThreadId { get; init; }

    public override string JoinKey => OsThreadId.ToString();
}
