using DumpDetective.Analysis.SdkBridge;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Pipeline;

/// <summary>
/// Shared <see cref="IThreadStackScanParticipant"/> state for analyzers that consume the pipeline's
/// single shared thread-stack walk (<see cref="ThreadStackScanDispatcher"/>) instead of walking every
/// thread's stack independently. Falls back to a normal live query
/// (<see cref="BuildPrecomputedQuery"/> returning <c>null</c>) when invoked outside the pipeline
/// (tests, benchmarks — no shared scan ever ran).
/// </summary>
internal sealed class ThreadStackScanState
{
    private Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>? _threadsByOsId;
    private Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>? _framesByOsId;
    private bool _scanSucceeded;

    public void BeforeThreadStackScan()
    {
        _threadsByOsId = new Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>();
        _framesByOsId = new Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>();
    }

    public void OnThreadStack(in ThreadStackSnapshot snapshot, int stackRootCount)
    {
        ClrThread thread = snapshot.Thread;
        _threadsByOsId![thread.OSThreadId] = ThreadStackTranslator.ToThreadRef(thread, stackRootCount);

        var frames = new List<Sdk.Analysis.ThreadStackFrameRef>(snapshot.TopFrames.Count);
        foreach (ClrStackFrame frame in snapshot.TopFrames)
            frames.Add(ThreadStackTranslator.ToFrameRef(frame));
        _framesByOsId![thread.OSThreadId] = frames;
    }

    public void OnThreadStackScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    public Sdk.Analysis.IRuntimeThreadQuery? BuildPrecomputedQuery() =>
        _scanSucceeded ? new PrecomputedRuntimeThreadQuery(_threadsByOsId!, _framesByOsId!) : null;
}
