namespace DumpDetective.Core.Enums;

/// <summary>
/// Per-node report-filter tag for the dominator tree (see
/// docs/analysis/phase1-redesigns/dominator-tree-lengauer-tarjan.md §D1) — decides which rows the
/// report surfaces, never graph/node membership. Gen0/Gen1 nodes are full members of the tree
/// (§D1: whole reachable heap, not Gen2/LOH-scoped edges); this tag only controls display.
/// </summary>
public enum GenerationTag
{
    Gen0,
    Gen1,
    Gen2,
    Loh,
    Poh,
    Frozen,
    Unknown,
}
