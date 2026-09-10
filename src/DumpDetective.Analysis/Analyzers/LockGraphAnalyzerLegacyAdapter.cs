using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="LockGraphAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-thread-quartet-plan.md.
/// </summary>
/// <remarks>
/// Also implements <see cref="IThreadStackScanParticipant"/> directly — not the inner
/// <see cref="LockGraphAnalyzer"/>, which the pipeline never sees — so this analyzer keeps
/// participating in the pipeline's single shared thread-stack walk
/// (<c>ThreadStackScanDispatcher</c>) alongside the still-legacy rest of the thread-domain quartet,
/// exactly as it did pre-retyping. <c>OnThreadStack</c> (explicit interface implementation — its
/// <see cref="ThreadStackSnapshot"/> parameter is <c>internal</c>, so it can't be a public member,
/// same reason the pre-retyping analyzer itself used explicit implementation here) translates each
/// thread's captured frames into SDK shapes (<see cref="ThreadStackTranslator"/>) as they arrive;
/// when the pipeline later calls <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}.AnalyzeAsync"/>,
/// <see cref="BuildSdkContext"/> hands the inner SDK analyzer a
/// <see cref="PrecomputedRuntimeThreadQuery"/> backed by that already-accumulated data instead of a
/// live query that would walk every thread's stack a second time. Falls back to a normal live query
/// when invoked outside the pipeline (tests, benchmarks — no shared scan ever ran), matching the
/// pre-retyping analyzer's own <c>_participantScanSucceeded</c> fallback exactly.
/// </remarks>
public sealed class LockGraphAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<LockGraphAnalyzer>, IThreadStackScanParticipant
{
    private Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>? _threadsByOsId;
    private Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>? _framesByOsId;
    private bool _scanSucceeded;

    public LockGraphAnalyzerLegacyAdapter() : base(new LockGraphAnalyzer())
    {
    }

    public int GetRequiredFrameCount(AnalysisContext context) => LockGraphAnalyzer.FrameScanDepth;

    public void BeforeThreadStackScan(AnalysisContext context)
    {
        _threadsByOsId = new Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>();
        _framesByOsId = new Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>();
    }

    void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot)
    {
        ClrThread thread = snapshot.Thread;
        _threadsByOsId![thread.OSThreadId] = ThreadStackTranslator.ToThreadRef(thread, stackRootCount: 0);

        var frames = new List<Sdk.Analysis.ThreadStackFrameRef>(snapshot.TopFrames.Count);
        foreach (ClrStackFrame frame in snapshot.TopFrames)
            frames.Add(ThreadStackTranslator.ToFrameRef(frame));
        _framesByOsId![thread.OSThreadId] = frames;
    }

    public void OnThreadStackScanCompleted(bool succeeded) => _scanSucceeded = succeeded;

    protected override Sdk.Analysis.AnalysisContext BuildSdkContext(AnalysisContext context)
    {
        Sdk.Analysis.IRuntimeThreadQuery? precomputed = _scanSucceeded
            ? new PrecomputedRuntimeThreadQuery(_threadsByOsId!, _framesByOsId!)
            : null;
        return LegacyAnalysisContextTranslator.Translate(context, ResolveOptions(context.AnalysisOptions), precomputed);
    }
}
