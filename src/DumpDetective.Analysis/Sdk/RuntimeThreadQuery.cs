using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>runtime.threads</c> capability, built for
/// <c>JitAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// </summary>
internal sealed class RuntimeThreadQuery(ClrRuntime runtime, IHeapAnalysisCache? cache) : IRuntimeThreadQuery
{
    // Matches ThreadAnalyzer's own UnboundedFrameCount precedent (§11.4 M8) — measured no cost
    // concern walking a real dump's deepest stack, so no artificial cap here either.
    private const int MaxStackRootsToCount = 100_000;

    public IEnumerable<RuntimeThreadRef> EnumerateThreads()
    {
        foreach (ClrThread thread in runtime.Threads)
        {
            int stackRootCount = cache?.GetOrCountThreadStackRoots(thread, MaxStackRootsToCount) ?? 0;
            yield return ThreadStackTranslator.ToThreadRef(thread, stackRootCount);
        }
    }

    private Dictionary<uint, ClrThread>? _threadsByOsId;

    public IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread)
    {
        _threadsByOsId ??= BuildThreadsByOsId();
        if (!_threadsByOsId.TryGetValue(thread.Thread.OsThreadId, out ClrThread? clrThread))
            yield break;

        foreach (ClrStackFrame frame in clrThread.EnumerateStackTrace())
            yield return ThreadStackTranslator.ToFrameRef(frame);
    }

    private Dictionary<uint, ClrThread> BuildThreadsByOsId()
    {
        var map = new Dictionary<uint, ClrThread>();
        foreach (ClrThread thread in runtime.Threads)
            map.TryAdd(thread.OSThreadId, thread);
        return map;
    }
}
