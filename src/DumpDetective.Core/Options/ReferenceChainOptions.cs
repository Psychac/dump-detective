namespace DumpDetective.Core.Options;

public sealed class ReferenceChainOptions
{
    public int TopCount { get; init; } = 10;

    // ── Bidirectional search budget ────────────────────────────────────────────
    // Bounds a single index-backed bidirectional root-path search (see
    // IndexBackedBidirectionalSearch / BidirectionalGraphSearch), run once per top-N type sample
    // (TopCount above) — not per heap object. Kept as real limits, not deleted: the reverse-edge
    // index itself is now complete (MaxParentsPerChild removed, see
    // docs/analysis/phase1-redesigns/dominator-tree-phase1-integration.md §3), but a single hub
    // object's measured real-world fan-in (346K on a 3.3GB dump, 10.76M on a 25.6GB dump — same
    // doc) would make an unbounded per-query expansion of that hub during a live search
    // intractable, unlike a one-time linear index build. See docs/refactor/
    // analysis-profile-removal-plan.md §9.20 implementation notes for the full reasoning on why
    // this keeps the analyzer AMBER rather than GREEN.
    public int MaxCandidateNodes { get; init; } = 50_000;
    public int MaxCandidateDepth { get; init; } = 8;
    public int MaxRootExpansionDepth { get; init; } = 12;
    public int LargeFanoutThreshold { get; init; } = 100;

    public IReadOnlyList<string> KnownLeakTypePatterns { get; init; } =
    [
        "System.Collections.Generic.List",
        "System.Collections.Generic.Dictionary",
        "Newtonsoft.Json",
    ];
}
