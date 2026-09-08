using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Pipeline;

internal readonly struct ThreadStackSnapshot
{
    public required ClrThread Thread { get; init; }

    // Valid only for the duration of the OnThreadStack call — the dispatcher reuses one
    // buffer across threads. Copy into a new list if a participant needs to retain frames.
    public required IReadOnlyList<ClrStackFrame> TopFrames { get; init; }
}
