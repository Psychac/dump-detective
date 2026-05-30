using DumpDetective.Core.Abstractions;
using DumpDetective.Core.Models;
using System.Diagnostics;
using System.Runtime;

namespace DumpDetective.Analysis.Pipeline;

internal sealed class AnalyzerCleanupPolicy
{
    // Cached once to avoid repeated OS process lookup per analyzer cleanup.
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    public void CleanupAfterAnalyzer(
        RuntimeAnalysisContext context,
        IAnalyzer analyzer,
        int completedAnalyzerCount,
        long workingSetBefore,
        bool trackWorkingSet)
    {
        try
        {
            if (analyzer is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Best effort only.
                }
            }

            if (context.Diagnostics is null)
                return;

            long workingSetAfter = trackWorkingSet ? GetWorkingSet() : 0;
            if (!context.Diagnostics.ShouldCollectAfterAnalyzerRun(completedAnalyzerCount, workingSetBefore, workingSetAfter))
                return;

            try
            {
                if (context.Diagnostics.CompactLargeObjectHeapAfterAnalyzerCollection)
                {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch
            {
                // Best effort only.
            }
        }
        catch
        {
            // Swallow cleanup policy errors so analysis results are preserved.
        }
    }

    private static long GetWorkingSet()
    {
        CurrentProcess.Refresh();
        return CurrentProcess.WorkingSet64;
    }
}