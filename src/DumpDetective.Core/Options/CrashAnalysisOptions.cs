namespace DumpDetective.Core.Options
{
    /// <summary>
    /// Options that control the behavior of the CrashAnalyzer.
    /// </summary>
    public sealed class CrashAnalysisOptions
    {
        /// <summary>
        /// Caps how many exception instances of the same type get their full detail (message,
        /// HResult, inner-exception type, chain depth, original stack trace) extracted during the
        /// heap scan. Active-thread exceptions are always extracted regardless of this cap. Unlike
        /// this class's other former knobs (pure report-width limits, moved to the render layer),
        /// this one gates genuinely expensive per-object work — <c>ExtractExceptionInfo</c> walks
        /// the inner-exception chain and parses the original stack trace — and the resulting
        /// <c>ExceptionInstance</c> holds full stack-trace string lists, so uncapping it would
        /// materialize that per-instance detail for every exception object of a hot type. Every
        /// reported total/count (<c>TotalExceptions</c>, per-type counts, generation counts) is
        /// already computed unconditionally elsewhere and is unaffected by this cap. Kept as a fixed
        /// constant, not tier-varied.
        /// </summary>
        public int MaxExceptionsPerType { get; init; } = 10;

        /// <summary>
        /// Wall-clock budget (milliseconds) for Gen2/LOH exception retention-path enrichment —
        /// bounds total <c>RootPathFinder</c> BFS time across the whole candidate set, the same
        /// role <see cref="EventLeakOptions.MaxEvidenceEnrichmentMs"/> plays for
        /// <c>EventLeakAnalyzer</c>. Retention-path search is real per-object work (unlike the
        /// unconditional totals/counts elsewhere in this analyzer), so it needs its own budget
        /// rather than relying on <see cref="MaxExceptionsPerType"/> alone.
        /// </summary>
        public int MaxRetentionPathEnrichmentMs { get; init; } = 2000;
    }
}
