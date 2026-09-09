using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Single-pass streaming extraction of the <c>trace.contention</c> section — one record per
/// lock-contention episode, paired from <see cref="ContentionStartTraceData"/>/
/// <see cref="ContentionStopTraceData"/> per thread.
/// </summary>
/// <remarks>
/// <b>Why duration is computed, not read from the payload, verified against real data
/// (<c>tools/GcContentionEventProbe</c> against a real 912.1 MB <c>.etl</c>, 64,782
/// start/stop pairs):</b> <see cref="ContentionStopTraceData.DurationNs"/> is only populated for
/// <c>Version &gt;= 1</c> payloads; every real event observed in that capture, both
/// <see cref="ContentionFlags.Native"/> and <see cref="ContentionFlags.Managed"/>, was
/// <c>Version == 0</c>, so the field reads as a constant zero rather than a real duration. Pairing
/// <c>ContentionStart</c>/<c>ContentionStop</c> by (process, thread) — a thread can only be
/// blocked on one contention episode at a time — and computing the duration from their own
/// timestamps is the only reliable source in this data, not a stylistic preference over trusting
/// the payload.
/// </remarks>
internal static class ContentionIndexer
{
    public static long Write(Stream sectionStream, string tracePath, int? targetProcessId, CancellationToken cancellationToken)
    {
        using var writer = new ContentionIndexWriter(sectionStream);
        var pendingStarts = new Dictionary<(int ProcessId, int ThreadId), double>();

        using TraceEventDispatcher source = TraceSourceOpener.Open(tracePath);

        source.Clr.ContentionStart += data =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            pendingStarts[(data.ProcessID, data.ThreadID)] = data.TimeStampRelativeMSec;
        };

        source.Clr.ContentionStop += data =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            var key = (data.ProcessID, data.ThreadID);
            // A stop with no matching start is rundown truncation (contention began before
            // capture start) — there is no reliable duration to report, so it's skipped rather
            // than guessed, the same treatment TraceMethodIndexer gives an unresolved rundown gap.
            if (!pendingStarts.Remove(key, out double startMs))
                return;

            double stopMs = data.TimeStampRelativeMSec;
            writer.Add(
                startTicks: (long)(startMs * TimeSpan.TicksPerMillisecond),
                durationTicks: (long)((stopMs - startMs) * TimeSpan.TicksPerMillisecond),
                threadId: data.ThreadID,
                flags: (byte)data.ContentionFlags);
        };

        source.Process();

        return writer.Flush();
    }
}
