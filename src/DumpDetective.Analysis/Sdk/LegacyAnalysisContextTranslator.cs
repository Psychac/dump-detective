namespace DumpDetective.Analysis.SdkBridge;

/// <summary>
/// Builds an <c>Sdk.Analysis.AnalysisContext</c> from the legacy <c>Core.Abstractions.AnalysisContext</c>
/// a <see cref="LegacyAnalyzerAdapter{TSdkAnalyzer}"/> receives from the existing pipeline. Only
/// populates the capabilities a migrated analyzer actually needs (<c>HeapTypeStatistics</c>,
/// <c>HeapSegments</c> — see docs/refactor/modularity/phase-1-full-extraction-retyping-plan.md).
/// Every other capability-scoped property on the built context is left null, which is correct here
/// (not merely incomplete): no migrated analyzer needs them yet, and the SDK context's own contract
/// already treats null as "not available for this session."
/// </summary>
internal static class LegacyAnalysisContextTranslator
{
    public static Sdk.Analysis.AnalysisContext Translate(Core.Abstractions.AnalysisContext legacy, object? analyzerOptions) =>
        new()
        {
            Observations = NoOpObservationSink.Instance,
            Progress = WrapProgress(legacy.Progress),
            AnalyzerOptions = analyzerOptions,
            HeapTypeStatistics = new HeapTypeStatisticsQuery(legacy.Heap, legacy.Cache),
            HeapSegments = new HeapSegmentQuery(legacy.Heap, legacy.Runtime, legacy.Cache),
        };

    private static IProgress<Sdk.Analysis.AnalyzerProgressReport>? WrapProgress(IProgress<Core.Abstractions.AnalyzerProgressReport>? progress) =>
        progress is null
            ? null
            : new Progress<Sdk.Analysis.AnalyzerProgressReport>(p =>
                progress.Report(new Core.Abstractions.AnalyzerProgressReport(p.ScannedCount, p.Phase, p.Detail, p.Elapsed)));
}
