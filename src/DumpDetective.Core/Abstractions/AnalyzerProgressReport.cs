namespace DumpDetective.Core.Abstractions;

/// <summary>
/// A lightweight, analyzer-owned progress snapshot reported via
/// <see cref="System.IProgress{T}"/> during <c>AnalyzeAsync</c>.
/// Analyzers construct this directly — no pipeline plumbing, RunId, or sink knowledge required.
/// The pipeline translates it to a full <c>AnalysisDiagnosticsEvent</c> before publishing.
/// </summary>
/// <param name="ScannedCount">Objects (or items) scanned so far. 0 is valid for phase-label-only reports.</param>
/// <param name="Phase">
/// Human-readable current work phase, e.g. "scanning heap", "building type index", "tracing roots", "aggregating results".
/// A phase change triggers a visible label transition in the console output.
/// </param>
/// <param name="Detail">
/// Optional supplementary detail shown on the live scan line, e.g. "12 leaks found", "42/100 types".
/// Keep it short — it appears inline next to the scan rate.
/// </param>
/// <param name="Elapsed">
/// Optional elapsed time since the operation started. Used by console renderers to compute
/// an accurate scan rate. Null when elapsed is not meaningful (phase-label-only reports).
/// </param>
public sealed record AnalyzerProgressReport(
    long ScannedCount,
    string Phase,
    string? Detail = null,
    TimeSpan? Elapsed = null);

