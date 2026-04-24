namespace DumpDetective.Core.Options;

/// <summary>
/// Controls the search strategy used by the reference-chain analyzer.
/// </summary>
public enum ReferenceChainSearchMode
{
    /// <summary>
    /// Depth-limited forward BFS only. Fastest, lowest memory, may miss long paths.
    /// </summary>
    Fast = 0,

    /// <summary>
    /// Bidirectional search with a scoped candidate set and partial reverse index.
    /// Good balance of accuracy and resource use.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// Bidirectional search with a larger candidate set and deeper traversal.
    /// Most accurate; higher memory and CPU usage.
    /// </summary>
    Deep = 2,
}

public sealed class ReferenceChainOptions
{
    public int TopCount { get; init; } = 5;

    /// <summary>
    /// Search strategy mode. Defaults to <see cref="ReferenceChainSearchMode.Balanced"/>.
    /// </summary>
    public ReferenceChainSearchMode SearchMode { get; init; } = ReferenceChainSearchMode.Balanced;

    // ── Fast-mode limits ──────────────────────────────────────────────────────
    /// <summary>Max BFS nodes visited before giving up (Fast mode only).</summary>
    public int MaxPathSearchObjects { get; init; } = 5_000;

    // ── Balanced / Deep mode limits ───────────────────────────────────────────
    /// <summary>
    /// Maximum number of nodes allowed in the bidirectional candidate set.
    /// Balanced default: 50 000. Deep default: 200 000.
    /// </summary>
    public int MaxCandidateNodes { get; init; } = 0; // 0 = use mode default

    /// <summary>
    /// Maximum forward/reverse expansion depth when building the candidate set.
    /// Balanced default: 8. Deep default: 15.
    /// </summary>
    public int MaxCandidateDepth { get; init; } = 0; // 0 = use mode default

    /// <summary>
    /// Maximum BFS depth when expanding from roots during the constrained search phase.
    /// Balanced default: 12. Deep default: 25.
    /// </summary>
    public int MaxRootExpansionDepth { get; init; } = 0; // 0 = use mode default

    // ── Pruning options (all modes) ────────────────────────────────────────────
    public bool SkipArrays { get; init; } = true;
    public int LargeFanoutThreshold { get; init; } = 100;
    public IReadOnlyList<string> KnownLeakTypePatterns { get; init; } =
    [
        "System.Collections.Generic.List",
        "System.Collections.Generic.Dictionary",
        "Newtonsoft.Json",
    ];

    // ── Resolved per-mode defaults ─────────────────────────────────────────────
    internal int ResolvedMaxCandidateNodes => MaxCandidateNodes > 0 ? MaxCandidateNodes : SearchMode switch
    {
        ReferenceChainSearchMode.Deep => 200_000,
        _ => 50_000,
    };

    internal int ResolvedMaxCandidateDepth => MaxCandidateDepth > 0 ? MaxCandidateDepth : SearchMode switch
    {
        ReferenceChainSearchMode.Deep => 15,
        _ => 8,
    };

    internal int ResolvedMaxRootExpansionDepth => MaxRootExpansionDepth > 0 ? MaxRootExpansionDepth : SearchMode switch
    {
        ReferenceChainSearchMode.Deep => 25,
        _ => 12,
    };
}