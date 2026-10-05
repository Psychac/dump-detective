namespace DumpDetective.Sdk.Synthesis;

public enum FalsificationKind
{
    Capture,
    Windbg,
    CodeInspection,
}

/// <summary>A concrete way to show a claim wrong. Must be specific to the claim, not boilerplate.</summary>
public sealed record FalsificationTest
{
    public required FalsificationKind Kind { get; init; }

    public required string Test { get; init; }

    public string? Command { get; init; }
}
