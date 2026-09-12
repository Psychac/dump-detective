using DumpDetective.Sdk.Analysis;

using Microsoft.Diagnostics.Runtime;

namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Dump-side implementation of the <c>runtime.thread-pool</c> capability, built for
/// <c>HangAnalyzer</c>'s retyping (docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Ports the pre-retyping analyzer's own <c>ReadRuntimeThreadPool</c>/<c>GetIntProperty</c> unchanged
/// — <c>ClrThreadPool.QueueLength</c> needs a defensive reflection probe (not a public property on
/// every ClrMD version this project has run against), the same reason the pre-retyping analyzer
/// itself used reflection there instead of a direct property read.
/// </summary>
internal sealed class RuntimeThreadPoolQuery(ClrRuntime runtime) : IRuntimeThreadPoolQuery
{
    public bool TryGetThreadPoolInfo(out RuntimeThreadPoolInfo info)
    {
        ClrThreadPool? tp = runtime.ThreadPool;
        if (tp is null)
        {
            info = default;
            return false;
        }

        info = new RuntimeThreadPoolInfo(
            MinThreads: tp.MinThreads,
            MaxThreads: tp.MaxThreads,
            ActiveWorkerThreads: tp.ActiveWorkerThreads,
            IdleWorkerThreads: tp.IdleWorkerThreads,
            RetiredWorkerThreads: tp.RetiredWorkerThreads,
            QueueLength: GetIntProperty(tp, "QueueLength"),
            CpuUtilization: tp.CpuUtilization);
        return true;
    }

    private static int? GetIntProperty(object instance, string propertyName)
    {
        try
        {
            object? value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
            return value switch
            {
                int intValue => intValue,
                long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
