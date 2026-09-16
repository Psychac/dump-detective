using DumpDetective.Sdk.Identity;

namespace DumpDetective.Sdk.Analysis;

/// <summary>
/// One GC-suspend/restart pause window — see <see cref="ITraceGcPauseQuery"/>.
/// </summary>
/// <param name="Reason">Raw <c>GCSuspendEEReason</c> (<c>Microsoft.Diagnostics.Tracing.Parsers.Clr</c>)
/// ordinal — no SDK enum wraps it; a category, not a quantity, so it can't be a <c>Measure</c>
/// either, and it isn't more or less "severe" on its own (that's a synthesis-rule judgment, not a
/// raw fact).</param>
/// <param name="Generation">The single GC attributed to this pause window, or <c>null</c> when no
/// GC's own span was fully nested inside it (see <c>GcPauseIndexer</c>'s attribution rule — this is
/// an honest "no GC data available" for e.g. background GCs, not a guessed nearest match).</param>
/// <param name="HeapSizeBytes">The attributed GC's post-collection heap size, or <c>null</c> under
/// the same condition as <see cref="Generation"/>.</param>
public readonly record struct GcPauseRecord(
    ThreadRef Thread,
    TimeSpan StartProcessUptime,
    TimeSpan Duration,
    byte Reason,
    int? Generation,
    long? HeapSizeBytes);

/// <summary>The <c>trace.gc-events</c> capability.</summary>
public interface ITraceGcPauseQuery
{
    IEnumerable<GcPauseRecord> EnumeratePauses();
}
