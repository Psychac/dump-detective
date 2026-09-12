using DumpDetective.Analysis.Cache;
using DumpDetective.Analysis.Pipeline;
using DumpDetective.Analysis.SdkBridge;
using DumpDetective.Core.Abstractions;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.Analyzers.Threads;

/// <summary>
/// Runs the retyped <see cref="ThreadAnalyzer"/> through the existing
/// <c>Core.Abstractions.IAnalyzer</c> pipeline. See
/// docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md.
/// </summary>
/// <remarks>
/// Same shape as <c>LockGraphAnalyzerLegacyAdapter</c> (thread-quartet item 1 of 4 remaining) — also
/// implements <see cref="IThreadStackScanParticipant"/> directly so this analyzer keeps
/// participating in the pipeline's single shared thread-stack walk. Unlike
/// <c>LockGraphAnalyzerLegacyAdapter</c> (which hardcodes <c>stackRootCount: 0</c>, a fact it never
/// needed), this adapter computes a real stack-root count per thread via
/// <c>IHeapAnalysisCache.GetOrCountThreadStackRoots</c> during <see cref="OnThreadStack"/> — the
/// same call the pre-retyping analyzer's own <c>GetOrCountStackRoots</c> made, just performed once
/// per thread as the shared scan visits it rather than lazily cached by address.
/// </remarks>
public sealed class ThreadAnalyzerLegacyAdapter : LegacyAnalyzerAdapter<ThreadAnalyzer>, IThreadStackScanParticipant
{
    private IHeapAnalysisCache? _cache;
    private Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>? _threadsByOsId;
    private Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>? _framesByOsId;
    private bool _scanSucceeded;

    public ThreadAnalyzerLegacyAdapter() : base(new ThreadAnalyzer())
    {
    }

    public int GetRequiredFrameCount(AnalysisContext context) => ThreadAnalyzer.UnboundedFrameCount;

    public void BeforeThreadStackScan(AnalysisContext context)
    {
        _cache = context.Cache;
        _threadsByOsId = new Dictionary<uint, Sdk.Analysis.RuntimeThreadRef>();
        _framesByOsId = new Dictionary<uint, IReadOnlyList<Sdk.Analysis.ThreadStackFrameRef>>();
    }

    void IThreadStackScanParticipant.OnThreadStack(in ThreadStackSnapshot snapshot)
    {
        ClrThread thread = snapshot.Thread;
        int stackRootCount = _cache?.GetOrCountThreadStackRoots(thread, ThreadAnalyzer.UnboundedFrameCount) ?? 0;
        _threadsByOsId![thread.OSThreadId] = ThreadStackTranslator.ToThreadRef(thread, stackRootCount);

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
