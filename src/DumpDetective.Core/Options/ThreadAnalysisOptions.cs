namespace DumpDetective.Core.Options;

public sealed class ThreadAnalysisOptions
{
    // When true, prewarm stack-root counts in a background task instead of blocking
    // the main analysis thread. Default false for conservative behavior. Orthogonal
    // execution-scheduling policy, not an exactness knob — kept, not tier-varied.
    public bool PrewarmCacheInBackground { get; init; } = false;
}
