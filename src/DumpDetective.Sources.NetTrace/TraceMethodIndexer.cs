using DumpDetective.Sdk.Identity;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Single-pass streaming extraction of the <c>trace.methods</c> section from a real
/// <c>.etl</c>/<c>.nettrace</c> capture. Built on the raw event-callback API
/// (<see cref="TraceEventDispatcher.Process"/>), not <c>TraceLog.OpenOrConvert</c> — see
/// docs/refactor/modularity/phase-6-trace-source.md § Ingest for why. Verified end-to-end against a
/// real 912.1 MB <c>.etl</c> capture; architecturally identical for <c>.nettrace</c> (EventPipe)
/// but unverified against a real sample — none exists in this environment (see
/// docs/refactor/modularity-plan.md § 10 point 3 for the same kind of gap, same treatment: named,
/// not silently assumed away).
/// </summary>
internal static class TraceMethodIndexer
{
    /// <summary>
    /// Streams <paramref name="tracePath"/> once, extracting one <c>trace.methods</c> record per
    /// distinct <c>MethodID</c> observed for <paramref name="targetProcessId"/> (or every process,
    /// if <see langword="null"/>) via CLR method-load and rundown events. Returns the record count
    /// — the caller passes it to <c>CacheContainerWriter.EndSection</c>.
    /// </summary>
    public static long Write(Stream sectionStream, string tracePath, int? targetProcessId, CancellationToken cancellationToken)
    {
        using var writer = new TraceMethodIndexWriter(sectionStream);
        var seenMethodIds = new HashSet<long>();

        using TraceEventDispatcher source = TraceSourceOpener.Open(tracePath);

        void OnMethodEvent(MethodLoadUnloadVerboseTraceData data)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetProcessId is int pid && data.ProcessID != pid)
                return;

            if (!seenMethodIds.Add(data.MethodID))
                return;

            // The trace side gives us an authoritative flag for "no stable identity" (dynamic
            // methods — e.g. IL_STUB_PInvoke, reported under the synthetic "dynamicClass"
            // namespace) rather than requiring EntityCanonicalizer to guess from the name string,
            // which is the None-fidelity case its own doc remarks note it cannot detect on its own.
            // Confirmed a common real case, not an edge case: 2 of the first 5 real samples from
            // tools/MethodEventProbe were IL_STUB_PInvoke.
            string canonicalTypeName;
            MatchFidelity fidelity;
            if (data.IsDynamic)
            {
                canonicalTypeName = data.MethodNamespace;
                fidelity = MatchFidelity.None;
            }
            else
            {
                (canonicalTypeName, fidelity) = EntityCanonicalizer.CanonicalizeTypeName(data.MethodNamespace);
            }

            writer.Add(
                data.MethodID,
                data.ModuleID,
                data.MethodStartAddress,
                data.MethodSize,
                data.MethodToken,
                fidelity,
                canonicalTypeName,
                data.MethodName,
                data.MethodSignature);
        }

        // LoadVerbose: JIT'd during the trace. DCStart/DCStopVerboseV2: rundown events for methods
        // already loaded before tracing started/at trace end — the case
        // docs/refactor/modularity/phase-6-trace-source.md explicitly warns can be missing or
        // truncated; subscribing to both rundown directions maximizes real coverage rather than
        // silently relying on just one.
        source.Clr.MethodLoadVerbose += OnMethodEvent;
        source.Clr.MethodDCStartVerboseV2 += OnMethodEvent;
        source.Clr.MethodDCStopVerboseV2 += OnMethodEvent;

        source.Process();

        return writer.Flush();
    }
}
