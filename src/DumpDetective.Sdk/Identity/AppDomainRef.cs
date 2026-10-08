namespace DumpDetective.Sdk.Identity;

/// <summary>
/// A CLR AppDomain. <see cref="JoinKey"/> is its name, the one thing a dump's module inventory and its
/// thread list both report; the numeric domain id is a dump-local handle and stays out of the identity.
/// Two domains that share a name are therefore one entity.
/// </summary>
public sealed record AppDomainRef : EntityRef
{
    public override EntityKind Kind => EntityKind.AppDomain;

    public required string Name { get; init; }

    /// <summary>Dump-local handle — not part of <see cref="JoinKey"/>.</summary>
    public int? DomainId { get; init; }

    public override string JoinKey => Name;

    /// <summary>Delegates to <see cref="EntityRef.Equals(EntityRef?)"/> — see its remarks for why
    /// <see cref="DomainId"/> is deliberately excluded from equality.</summary>
    public bool Equals(AppDomainRef? other) => base.Equals(other);

    public override int GetHashCode() => base.GetHashCode();
}
