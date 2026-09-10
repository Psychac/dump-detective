using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Options;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers;

/// <summary>
/// Runs the retyped <see cref="ThreadStackClusterAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-thread-quartet-plan.md.
/// </summary>
/// <remarks>
/// Also implements <see cref="IThreadStackScanParticipant"/> directly — not the inner
/// <see cref="ThreadStackClusterAnalyzer"/>, which the pipeline never sees — so this analyzer keeps
/// participating in the pipeline's single shared thread-stack walk (<c>ThreadStackScanDispatcher</c>)
/// alongside the still-legacy rest of the thread-domain quartet, exactly as it did pre-retyping.
/// <c>OnThreadStack</c> translates each thread's captured frames into SDK shapes
/// (<see cref="ThreadStackTranslator"/>) as they arrive; when the pipeline later calls
/// <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}.AnalyzeAsync"/>, <see cref="BuildSdkContext"/> hands
/// the inner SDK analyzer a <see cref="PrecomputedRuntimeThreadQuery"/> backed by that
/// already-accumulated data instead of a live query that would walk every thread's stack a second
/// time. Falls back to a normal live query when invoked outside the pipeline (tests, benchmarks — no
/// shared scan ever ran), matching the pre-retyping analyzer's own fallback exactly.
/// </remarks>
public sealed class ThreadStackClusterAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ThreadStackClusterAnalyzer>, IThreadStackScanParticipant
{
    private Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>? _threadsByOsId;
    private Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>? _framesByOsId;
    private bool _scanSucceeded;

    public ThreadStackClusterAnalyzerLegacyAdapter() : base(new ThreadStackClusterAnalyzer())
    {
    }

    protected override object? ResolveOptions(AnalysisOptions options) => options.ThreadStackClusterAnalysis;

    public int GetRequiredFrameCount(AnalysisContext context) => ThreadAnalyzer.UnboundedFrameCount;

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
