using DumpDetective.Core.Abstractions;
using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

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
            yield return new RuntimeThreadRef(
                Thread: new ThreadRef { OsThreadId = thread.OSThreadId, ManagedThreadId = thread.ManagedThreadId, Fidelity = MatchFidelity.Exact },
                IsGCSuspendPending: (thread.State & ClrThreadState.TS_GCSuspendPending) != 0,
                StackRootCount: stackRootCount,
                IsAlive: thread.IsAlive);
        }
    }

    private Dictionary<uint, ClrThread>? _threadsByOsId;

    public IEnumerable<ThreadStackFrameRef> EnumerateStackFrames(RuntimeThreadRef thread)
    {
        _threadsByOsId ??= BuildThreadsByOsId();
        if (!_threadsByOsId.TryGetValue(thread.Thread.OsThreadId, out ClrThread? clrThread))
            yield break;

        foreach (ClrStackFrame frame in clrThread.EnumerateStackTrace())
        {
            if (frame.Kind != ClrStackFrameKind.ManagedMethod)
            {
                yield return new ThreadStackFrameRef(IsManagedMethod: false, HasMethod: false,
                    DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false, IsReadyToRun: false,
                    MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0, MethodDisplayName: "");
                continue;
            }

            ClrMethod? method = frame.Method;
            if (method is null)
            {
                yield return new ThreadStackFrameRef(IsManagedMethod: true, HasMethod: false,
                    DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false, IsReadyToRun: false,
                    MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0, MethodDisplayName: "");
                continue;
            }

            string typeName = method.Type?.Name ?? "Unknown";
            string moduleName = method.Type?.Module?.Name ?? "Unknown";
            HotColdRegions hcr = method.HotColdInfo;

            yield return new ThreadStackFrameRef(
                IsManagedMethod: true,
                HasMethod: true,
                DeclaringTypeName: typeName,
                ModuleName: moduleName,
                IsDynamicModule: method.Type?.Module?.IsDynamic == true,
                IsReadyToRun: method.CompilationType == MethodCompilationType.Ngen,
                MethodDesc: method.MethodDesc,
                NativeCodeAddress: method.NativeCode,
                HotSize: hcr.HotSize,
                ColdSize: hcr.ColdSize,
                MethodDisplayName: method.Signature ?? typeName + "." + (method.Name ?? "?"));
        }
    }

    private Dictionary<uint, ClrThread> BuildThreadsByOsId()
    {
        var map = new Dictionary<uint, ClrThread>();
        foreach (ClrThread thread in runtime.Threads)
            map.TryAdd(thread.OSThreadId, thread);
        return map;
    }
}
