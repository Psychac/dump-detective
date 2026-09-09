using Microsoft.Diagnostics.Tracing;

namespace DumpDetective.Sources.NetTrace;

/// <summary>
/// Single-pass streaming extraction of the <c>trace.cpu-samples</c> section — one record per
/// kernel CPU-sampling-profiler interrupt (<c>PerfInfoSample</c>), leaf instruction pointer only.
/// </summary>
/// <remarks>
/// <para>
/// Verified against the real 912.1 MB <c>.etl</c> with a throwaway probe
/// (<c>tools/CpuSampleProbe</c>): 1,959,983 <c>PerfInfoSample</c> events across 76 processes, one
/// (PID 8044) accounting for 501,430 of them — real, substantial per-process CPU sample volume to
/// resolve against.
/// </para>
/// <para>
/// <b>No stack walk here, deliberately.</b> The kernel also emits a paired
/// <c>StackWalkStack</c> event per sample (2,807,821 observed — more than one per sample, since
/// other kernel event kinds trigger stack walks too) carrying the full call stack, but consuming it
/// needs frame interning and, per <c>CacheSectionId.TraceCpuSamples</c>'s own remarks, an
/// address-range index built from method-load events — exactly the design work
/// <c>TraceMethodIndexer</c> named and deferred as <c>trace.stacks</c> when it shipped. Scoping to
/// the leaf frame alone (already present directly on <c>SampledProfileTraceData</c>, no
/// correlation needed) avoids that work while still resolving to a real method via
/// <c>CacheSectionId.TraceMethods</c>'s own address ranges.
/// </para>
/// <para>
/// <b>ETW only — a real gap, not just "unverified for .nettrace" like the other three
/// sections.</b> ETW's kernel <c>PerfInfoSample</c> carries the leaf instruction pointer directly.
/// EventPipe's equivalent, <c>Microsoft.Diagnostics.Tracing.EventPipe.ClrThreadSampleTraceData</c>
/// (decompiled and checked, not guessed), carries none at all — only a <c>Type</c> enum; the
/// address only exists on its separately-paired <c>ClrThreadStackWalk</c> event, which is exactly
/// the stack-walk correlation this indexer exists to avoid. So unlike <c>trace.methods</c> (same
/// CLR provider shape on both sources, genuinely just unverified against a real <c>.nettrace</c>
/// sample), a <c>.nettrace</c> capture cannot produce this section via the leaf-only shortcut at
/// all — it needs the deferred stack-walk design work regardless of source kind. Throws rather than
/// silently producing an empty section.
/// </para>
/// <para>
/// <b>Cross-process address collision — named, accepted risk, not fixed here.</b>
/// <see cref="CacheSectionId.TraceMethods"/> carries no <c>ProcessId</c> column, so
/// <c>CpuHotspotAnalyzer</c>'s address-range resolution is necessarily global across every process
/// in the trace, not scoped per-process. Two different processes' managed code could in principle
/// load at the same virtual address (ASLR is per-process) and be misattributed. Retrofitting
/// <c>ProcessId</c> onto the already-shipped <c>trace.methods</c> format is real, separate work;
/// the real-data probe above shows why this is a low-severity edge case in practice for now, not a
/// reason to block on it — one process (PID 8044) dominates non-idle samples 97.5% to 2.5% across
/// the other 74 non-idle processes combined in the sample capture.
/// </para>
/// </remarks>
internal static class CpuSampleIndexer
{
    public static long Write(Stream sectionStream, string tracePath, int? targetProcessId, CancellationToken cancellationToken)
    {
        using var writer = new CpuSampleIndexWriter(sectionStream);

        using TraceEventDispatcher source = TraceSourceOpener.Open(tracePath);

        if (source is not ETWTraceEventSource etwSource)
        {
            throw new NotSupportedException(
                "trace.cpu-samples can only be built from an .etl (ETW) capture — EventPipe's " +
                "ClrThreadSampleTraceData carries no instruction pointer, only its paired " +
                "ClrThreadStackWalk event does, which needs the stack-walk correlation this " +
                "leaf-only indexer deliberately doesn't implement. See this type's remarks.");
        }

        etwSource.Kernel.PerfInfoSample += data =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targetProcessId is int pid && data.ProcessID != pid) return;

            writer.Add(
                timestampTicks: (long)(data.TimeStampRelativeMSec * TimeSpan.TicksPerMillisecond),
                processId: data.ProcessID,
                threadId: data.ThreadID,
                instructionPointer: data.InstructionPointer);
        };

        source.Process();

        return writer.Flush();
    }
}
