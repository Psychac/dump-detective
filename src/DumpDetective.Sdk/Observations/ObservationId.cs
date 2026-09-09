namespace DumpDetective.Sdk.Observations;

/// <summary>Stable identifier for one <see cref="Observation"/>, used by <c>Finding.DerivedFrom</c>
/// and by trend/diff queries to re-identify the same observation across a temporal series.</summary>
public readonly record struct ObservationId(Guid Value)
{
    public static ObservationId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
