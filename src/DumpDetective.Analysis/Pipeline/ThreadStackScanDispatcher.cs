using Microsoft.Diagnostics.Runtime;
using DumpDetective.Core.Abstractions;

namespace DumpDetective.Analysis.Pipeline;

// Runs a single shared pass over runtime.Threads and fans each thread's stack frames out to
// every registered IThreadStackScanParticipant, so N participating analyzers share one
// EnumerateStackTrace() walk per thread instead of each enumerating independently.
// Mirrors HeapIndexScanDispatcher's per-participant failure isolation and Completed(bool) gate.
internal sealed class ThreadStackScanDispatcher
{
    public void Run(ClrRuntime runtime, AnalysisContext context, IReadOnlyList<IThreadStackScanParticipant> participants, int maxFramesPerThread, CancellationToken cancellationToken)
    {
        if (participants.Count == 0)
            return;

        bool[] failed = new bool[participants.Count];

        for (int i = 0; i < participants.Count; i++)
        {
            try
            {
                participants[i].BeforeThreadStackScan(context);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                failed[i] = true;
            }
        }

        // Reused across threads (Clear()-ed, not reallocated) — participants that need to retain
        // frames beyond the OnThreadStack call must copy from ThreadStackSnapshot.TopFrames.
        var frameBuffer = new List<ClrStackFrame>(maxFramesPerThread);

        foreach (ClrThread thread in runtime.Threads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            frameBuffer.Clear();

            // Only alive threads have a walkable stack; dead threads still get an OnThreadStack
            // callback (with empty TopFrames) so participants that tally all threads — not just
            // alive ones — don't need a second independent enumeration of runtime.Threads.
            if (thread.IsAlive)
            {
                foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
                {
                    if (frameBuffer.Count >= maxFramesPerThread)
                        break;
                    frameBuffer.Add(frame);
                }
            }

            var snapshot = new ThreadStackSnapshot { Thread = thread, TopFrames = frameBuffer };

            for (int i = 0; i < participants.Count; i++)
            {
                if (failed[i])
                    continue;

                try
                {
                    participants[i].OnThreadStack(in snapshot);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested is false)
                {
                    failed[i] = true;
                }
            }
        }

        for (int i = 0; i < participants.Count; i++)
            participants[i].OnThreadStackScanCompleted(succeeded: !failed[i]);
    }
}
