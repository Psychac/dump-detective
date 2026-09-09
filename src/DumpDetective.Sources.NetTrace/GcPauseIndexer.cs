using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Single-pass streaming extraction of the <c>trace.gcevents</c> section — one record per
/// GC-suspend/restart pause window, from the raw event-callback API, following
/// <see cref="TraceMethodIndexer"/>'s ingest shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "pause window" and not "GC" is the unit of record, verified against real data
/// (<c>tools/GcContentionEventProbe</c> against a real 912.1 MB <c>.etl</c>):</b>
/// <see cref="ClrTraceEventParser.GCSuspendEEStart"/>/<c>GCRestartEEStop</c> is the actual
/// application-observable pause (managed threads stopped); <see cref="GCStartTraceData"/>/
/// <see cref="GCEndTraceData"/> brackets the GC itself, which is <i>not</i> the same interval for
/// a background GC — its collection window commonly spans several separate short foreground pause
/// cycles (e.g. <c>SuspendForGCPrep</c>) rather than nesting inside a single one. The two event
/// families also don't share a correlation id: <c>GCSuspendEETraceData.Count</c> and
/// <see cref="GCStartTraceData.Count"/> are independent counters (observed non-equal for the same
/// logical pause in real data), so pairing has to be temporal, not by-id.
/// </para>
/// <para>
/// <b>The rule this indexer applies:</b> a completed GC (GCStart → GCStop → GCHeapStats, matched
/// by <see cref="GCStartTraceData.Count"/> within one process) is attributed to a pause window only
/// when its own timespan is <i>fully contained</i> in that window's
/// [SuspendEEStart, RestartEEStop] span. This is exact for blocking (non-concurrent) GCs, which do
/// nest fully, and correctly attributes nothing for background GCs, whose collection span exceeds
/// any single pause window — an honest "no GC data available" rather than a guessed nearest match,
/// the same principle <see cref="TraceMethodIndexer"/> applies to <c>IsDynamic</c> methods.
/// </para>
/// </remarks>
internal static class GcPauseIndexer
{
    public static long Write(Stream sectionStream, string tracePath, int? targetProcessId, CancellationToken cancellationToken)
    {
        using var writer = new GcPauseIndexWriter(sectionStream);
        var state = new Dictionary<int, ProcessState>();

        ProcessState GetState(int processId)
        {
            if (!state.TryGetValue(processId, out ProcessState? s))
            {
                s = new ProcessState();
                state[processId] = s;
            }
            return s;
        }

        using TraceEventDispatcher source = TraceSourceOpener.Open(tracePath);

        source.Clr.GCSuspendEEStart += data =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            ProcessState s = GetState(data.ProcessID);
            // A second SuspendEEStart before the pending one closes is a rundown/ordering anomaly
            // (never observed in real data) — the newer one wins rather than the pairing getting
            // stuck forever on a pause that never closes.
            s.PendingPause = new PendingPause(data.TimeStampRelativeMSec, data.ThreadID, (byte)data.Reason);
        };

        source.Clr.GCStart += data =>
        {
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            ProcessState s = GetState(data.ProcessID);
            s.OpenGcs[data.Count] = new OpenGc(data.TimeStampRelativeMSec, data.Depth);
        };

        source.Clr.GCStop += data =>
        {
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            ProcessState s = GetState(data.ProcessID);
            if (s.OpenGcs.Remove(data.Count, out OpenGc openGc))
                s.PendingHeapStats = new CompletedGc(openGc.StartMSec, data.TimeStampRelativeMSec, openGc.Depth);
        };

        source.Clr.GCHeapStats += data =>
        {
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            ProcessState s = GetState(data.ProcessID);
            // GCHeapStats carries no GC-number field of its own; it is emitted immediately after
            // GCStop for the same GC (verified adjacent in real data), so "the most recently
            // completed GC in this process" is the correct — not a guessed — correlation.
            if (s.PendingHeapStats is CompletedGc completed)
            {
                s.CompletedGcs.Add(completed with { HeapBytes = data.TotalHeapSize });
                s.PendingHeapStats = null;
            }
        };

        source.Clr.GCRestartEEStop += data =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            ProcessState s = GetState(data.ProcessID);
            if (s.PendingPause is not PendingPause pause)
                return; // RestartEEStop with no matching SuspendEEStart — rundown truncation.

            s.PendingPause = null;

            double pauseStartMs = pause.StartMSec;
            double pauseStopMs = data.TimeStampRelativeMSec;

            CompletedGc? attributed = null;
            int matchCount = 0;
            for (int i = s.CompletedGcs.Count - 1; i >= 0; i--)
            {
                CompletedGc gc = s.CompletedGcs[i];

                // Prune anything that ended before this window started — it can't nest in this or
                // any later window, since pause windows only move forward in time.
                if (gc.StopMSec < pauseStartMs)
                {
                    s.CompletedGcs.RemoveAt(i);
                    continue;
                }

                if (gc.StartMSec >= pauseStartMs && gc.StopMSec <= pauseStopMs)
                {
                    attributed = gc;
                    matchCount++;
                }
            }

            // More than one fully-nested GC in one pause window has never been observed in real
            // data and would make attribution ambiguous — treat it the same as zero matches
            // (no GC data) rather than picking one arbitrarily.
            bool hasGcData = matchCount == 1 && attributed is not null;

            writer.Add(
                timestampTicks: (long)(pauseStartMs * TimeSpan.TicksPerMillisecond),
                threadId: pause.ThreadId,
                reason: pause.Reason,
                pauseTicks: (long)((pauseStopMs - pauseStartMs) * TimeSpan.TicksPerMillisecond),
                hasGcData: hasGcData,
                generation: hasGcData ? attributed!.Value.Generation : 0,
                heapBytes: hasGcData ? attributed!.Value.HeapBytes : 0);

            if (hasGcData)
                s.CompletedGcs.Remove(attributed!.Value);
        };

        source.Process();

        return writer.Flush();
    }

    private readonly record struct PendingPause(double StartMSec, int ThreadId, byte Reason);
    private readonly record struct OpenGc(double StartMSec, int Depth);
    private readonly record struct CompletedGc(double StartMSec, double StopMSec, int Generation, long HeapBytes = 0);

    private sealed class ProcessState
    {
        public PendingPause? PendingPause;
        public CompletedGc? PendingHeapStats;
        public readonly Dictionary<int, OpenGc> OpenGcs = [];
        public readonly List<CompletedGc> CompletedGcs = [];
    }
}
