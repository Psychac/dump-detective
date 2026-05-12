namespace DumpDetective.Analysis.Models;

/// <summary>A type that appeared in or significantly grew within current leak results relative to baseline.</summary>
internal sealed record NewLeakSignal(
    string TypeName,
    double BaselineBytes,
    double CurrentBytes,
    string Source);