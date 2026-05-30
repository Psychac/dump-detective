using DumpDetective.Core.Options;

namespace DumpDetective.Analysis.Pipeline;

internal static class AnalyzerCollectionPolicyEvaluator
{
    public static bool HasCollectionPolicy(DiagnosticsOptions diagnostics)
        => diagnostics.CollectAfterAnalyzerRun
           || diagnostics.CollectAfterAnalyzerRunEveryKAnalyzers > 0
           || diagnostics.CollectAfterAnalyzerRunWorkingSetThresholdBytes > 0;

    public static bool ShouldCollectAfterAnalyzerRun(
        DiagnosticsOptions diagnostics,
        int completedAnalyzerCount,
        long workingSetBeforeAnalyzer,
        long workingSetAfterAnalyzer)
    {
        if (diagnostics.CollectAfterAnalyzerRun)
            return true;

        if (completedAnalyzerCount > 0
            && diagnostics.CollectAfterAnalyzerRunEveryKAnalyzers > 0
            && completedAnalyzerCount % diagnostics.CollectAfterAnalyzerRunEveryKAnalyzers == 0)
        {
            return true;
        }

        if (diagnostics.CollectAfterAnalyzerRunWorkingSetThresholdBytes > 0)
        {
            long workingSetDelta = workingSetAfterAnalyzer - workingSetBeforeAnalyzer;
            if (workingSetAfterAnalyzer >= diagnostics.CollectAfterAnalyzerRunWorkingSetThresholdBytes
                || workingSetDelta >= diagnostics.CollectAfterAnalyzerRunWorkingSetThresholdBytes)
            {
                return true;
            }
        }

        return false;
    }
}