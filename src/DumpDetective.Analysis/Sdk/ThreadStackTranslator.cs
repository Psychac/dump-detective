using DumpDetective.Sdk.Analysis;
using DumpDetective.Sdk.Identity;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// The single place that maps ClrMD's <c>ClrThread</c>/<c>ClrStackFrame</c> onto the SDK's
/// <see cref="RuntimeThreadRef"/>/<see cref="ThreadStackFrameRef"/> — shared by the live
/// <see cref="RuntimeThreadQuery"/> and by the thread-domain quartet's
/// <c>IThreadStackScanParticipant</c>-implementing adapters (e.g. <c>LockGraphAnalyzerLegacyAdapter</c>),
/// which translate frames on the fly while accumulating the pipeline's shared thread-stack scan
/// instead of doing a second, independent walk. One translation, two call sites — extracted once the
/// second real consumer existed (docs/refactor/modularity/phase-1-thread-quartet-plan.md), not
/// speculatively ahead of one.
/// </summary>
internal static class ThreadStackTranslator
{
    public static RuntimeThreadRef ToThreadRef(ClrThread thread, int stackRootCount)
    {
        ClrException? currentException = thread.CurrentException;
        ulong stackSizeBytes = thread.StackBase > thread.StackLimit ? thread.StackBase - thread.StackLimit : 0;

        return new(
            Thread: new ThreadRef { OsThreadId = thread.OSThreadId, ManagedThreadId = thread.ManagedThreadId, Fidelity = MatchFidelity.Exact },
            IsGCSuspendPending: (thread.State & ClrThreadState.TS_GCSuspendPending) != 0,
            StackRootCount: stackRootCount,
            IsAlive: thread.IsAlive,
            LockCount: (int)thread.LockCount,
            IsGc: thread.IsGc,
            IsFinalizer: thread.IsFinalizer,
            IsThreadpoolWorker: (thread.State & ClrThreadState.TS_TPWorkerThread) != 0,
            IsCompletionPortThread: (thread.State & ClrThreadState.TS_CompletionPortThread) != 0,
            IsBackground: (thread.State & ClrThreadState.TS_Background) != 0,
            ThreadStateDisplay: FormatThreadState(thread.State),
            GcModeDisplay: thread.GCMode.ToString(),
            AppDomainName: thread.CurrentAppDomain?.Name,
            HasCurrentException: currentException is not null,
            ExceptionTypeName: currentException?.Type?.Name,
            ExceptionMessage: currentException?.Message,
            StackSizeBytes: stackSizeBytes);
    }

    // Ported unchanged from the pre-retyping ThreadAnalyzer's own FormatThreadState -- bit-flag
    // decomposition of ClrThreadState into a readable "Flag1 | Flag2" string, with any unrecognized
    // remaining bits rendered as a hex tail rather than silently dropped.
    private static string FormatThreadState(ClrThreadState state)
    {
        ulong raw = Convert.ToUInt64(state);
        if (raw == 0)
            return "0";

        var parts = new List<string>(capacity: 4);
        ulong remaining = raw;

        foreach (ClrThreadState flag in Enum.GetValues<ClrThreadState>())
        {
            ulong flagValue = Convert.ToUInt64(flag);
            if (flagValue == 0)
                continue;

            if ((remaining & flagValue) == flagValue)
            {
                parts.Add(flag.ToString());
                remaining &= ~flagValue;
            }
        }

        if (parts.Count == 0)
            return $"0x{raw:X}";

        if (remaining != 0)
            parts.Add($"0x{remaining:X}");

        return string.Join(" | ", parts);
    }

    public static ThreadStackFrameRef ToFrameRef(ClrStackFrame frame)
    {
        if (frame.Kind != ClrStackFrameKind.ManagedMethod)
        {
            return new ThreadStackFrameRef(IsManagedMethod: false, HasMethod: false,
                DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false, IsReadyToRun: false,
                MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0, MethodDisplayName: "",
                FrameName: frame.FrameName);
        }

        ClrMethod? method = frame.Method;
        if (method is null)
        {
            return new ThreadStackFrameRef(IsManagedMethod: true, HasMethod: false,
                DeclaringTypeName: "", ModuleName: "", IsDynamicModule: false, IsReadyToRun: false,
                MethodDesc: 0, NativeCodeAddress: 0, HotSize: 0, ColdSize: 0, MethodDisplayName: "",
                FrameName: frame.FrameName);
        }

        string typeName = method.Type?.Name ?? "Unknown";
        string moduleName = method.Type?.Module?.Name ?? "Unknown";
        HotColdRegions hcr = method.HotColdInfo;

        return new ThreadStackFrameRef(
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
            MethodDisplayName: method.Signature ?? typeName + "." + (method.Name ?? "?"),
            FrameName: frame.FrameName,
            RawMethodSignature: method.Signature);
    }
}
