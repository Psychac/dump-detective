namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A managed module/assembly. <see cref="JoinKey"/> is the simple assembly name — version, culture
/// and public key token are deliberately excluded, since a dump and a trace of the same running
/// process can otherwise disagree on those for reasons that don't affect identity (e.g. a trace
/// capturing a module load event before a redirect is fully resolved).
/// </summary>
public sealed record ModuleRef : EntityRef
{
    public override EntityKind Kind => EntityKind.Module;

    public required string SimpleName { get; init; }

    /// <summary>Dump-local handle — not part of <see cref="JoinKey"/>.</summary>
    public ulong? ModuleAddress { get; init; }

    public override string JoinKey => SimpleName;
}
