using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Centralises mapping and classification logic for <see cref="ClrSegment"/> kinds.
/// Other analyzers should prefer this to ad-hoc string-based checks.
/// </summary>
internal static class SegmentKindMapper
{
    public static HeapSegmentKind Map(ClrSegment segment)
    {
        string k = segment.Kind.ToString();
        if (k.Contains("Large", StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.LargeObjectHeap;
        if (k.Contains("Pinned", StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.PinnedObjectHeap;
        if (k.Contains("Frozen", StringComparison.OrdinalIgnoreCase)) return HeapSegmentKind.Frozen;
        return HeapSegmentKind.SmallObjectHeap;
    }

    public static bool IsEphemeral(ClrSegment segment)
    {
        string kindName = segment.Kind.ToString();
        if (kindName.Contains("Ephemeral", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!kindName.Contains("Large", StringComparison.OrdinalIgnoreCase)
            && !kindName.Contains("Pinned", StringComparison.OrdinalIgnoreCase)
            && !kindName.Contains("Frozen", StringComparison.OrdinalIgnoreCase)
            && segment.Generation0.Length > 0)
            return true;

        return false;
    }
}
