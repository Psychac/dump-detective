# Shared collapsible tree widget — design

## Motivation

Three report sections have (or will have) a branching, variable-depth "convergent paths"
shape that today is either rendered as a flat list or not rendered as a tree at all:

- **Thread Stack Cluster Analyzer** (P3-2, not yet built) — shared-prefix trie over
  per-cluster call-stack signatures; groups threads by their common blocking point
  even when reached via different call sites.
  See [thread-stack-cluster-analyzer-audit.md](../analysis/phase1/thread-stack-cluster-analyzer-audit.md) P3-2.
- **GC Root Analyzer** `RootPathGroups` (built) — currently renders every root path as an
  independent linear chain card, even when hundreds of paths share a long common suffix
  near the GC root. See [gcroot-analyzer-audit.md](../analysis/phase1/gcroot-analyzer-audit.md).
- **Dominator Analyzer** dominator tree data (built, `DominatorRetainedSetAggregator`) — an
  actual parent/child dominator tree with retained bytes per node; currently only surfaced
  as a flat top-N table, not as a tree. See [dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md).

Rather than building a bespoke renderer for thread-cluster P3-2 and re-solving the same
problem twice more later, build one generic widget and adopt it in the other two spots
opportunistically once it's proven.

## Scope of this change

The widget and its first consumer (Thread Stack Cluster Analyzer P3-2) are implemented —
`buildTreeWidget()` in `report.renderers.shared.js`, `TreeNode`/`TreeWidget` typed slots in
`AnalyzerDetailSection`, and `ThreadStackClusterAnalyzer.BuildClusterTree` (shared-prefix
trie with chain collapsing and breadth capping). The two other consumers below are not
touched yet — those remain tracked as prospective follow-ups (see "Adoption tracking")
and should be picked up as their own separate changes.

## Data contract

The widget takes a plain, serializable node shape — no analyzer-specific fields leak into
the renderer. Each domain result maps its own tree into this shape at the section-builder
layer (C#), not in JS:

```ts
interface TreeNode {
  label: string;            // display text for this node's own contribution (already truncated/formatted)
  count?: number;           // optional badge, e.g. thread count / path count / retained bytes
  countUnit?: string;       // optional suffix for count, e.g. "threads", "B"
  children?: TreeNode[];    // omitted/empty for leaves
  truncatedChildCount?: number; // if children were capped, how many more exist
  isChain?: boolean;        // true if this node represents a collapsed run of single-child nodes
}
```

Producers are responsible for:
- **Chain collapsing** — merging runs of single-child nodes into one `isChain: true` node
  before handing data to the widget (the widget does not do graph algorithms, only rendering).
- **Breadth capping** per node, populating `truncatedChildCount` — same convention as the
  existing `MaxSampleIdsPerClusterToShow`-style caps used elsewhere in the reporting layer.
- **Depth capping** if needed for a given data source; the widget renders whatever tree it's given.

## Rendering approach

Built as `buildTreeWidget(rootNodes, options)` in `report.renderers.shared.js`, next to the
other reusable DOM builders already there. Reuses the existing `<details>`/`<summary>`
disclosure pattern (`data-collapsible` attribute convention, same as `root-path-outer` and
`typed-slot--root-paths` in `report.renderers.sections.js`) so it inherits existing
collapse/expand CSS and keyboard behavior instead of introducing a new interaction model.

```js
// report.renderers.shared.js
function buildTreeWidget(rootNodes, options) {
  // options: { widgetClass, formatCount(node) -> string, onLeafClick?(node) }
  // returns a DOM fragment: one <details class="tree-node"> per non-leaf node,
  // recursing into .children; leaves render as plain rows, not <details>.
}
```

Each consumer calls it with its own `widgetClass` (e.g. `thread-cluster-tree`,
`root-path-tree`, `dominator-tree`) so CSS can still apply consumer-specific accents
(severity coloring, etc.) while sharing the structural/indentation rules in
`report.detail.css` under a shared `.tree-node` / `.tree-node__children` block.

## Constraints carried over from the existing codebase

- **No new JS test coverage exists for this today** — `ReportingVisualsTests.cs` only does
  string-contains checks against the `.js` source, no DOM/browser execution. The widget
  ships with the same limitation; correctness is verified by manual report inspection plus
  string-presence tests for key markers (`tree-node`, `data-collapsible="tree"`), consistent
  with how `RootPathGroups` rendering is tested today.
- **Search/TOC integration is out of scope for v1.** `report.ui.search.js` and
  `report.ui.toc.js` assume flat sections/tables; tree nodes will not be search-indexed or
  TOC-linked initially. Flagged here so it isn't silently forgotten, not because it needs
  solving now.
- Static, dependency-free vanilla JS only, consistent with the rest of `report.renderers.*.js`.

## Adoption tracking

Each of the three consumers should be picked up as its own follow-up item once the shared
widget exists:

1. Thread Stack Cluster Analyzer P3-2 — first consumer, drives the initial widget shape.
2. GC Root Analyzer `RootPathGroups` — collapse shared-suffix chains once the widget exists
   (tracked as a new P3 row in [gcroot-analyzer-audit.md](../analysis/phase1/gcroot-analyzer-audit.md)).
3. Dominator Analyzer dominator-tree rendering — **✅ DONE 2026-08-27**, third adopter: per-type
   dominance chains (P3-3, `DominatorAnalyzer.BuildDominatorChain` walking
   `IDominatorTreeProvider.TryGetImmediateDominator`) rendered via `buildTreeWidget` in the
   Gen2/LOH sub-table. Scoped to one chain per candidate row (each hop its own `TreeNode`,
   `IsChain: true`), not a merged multi-branch tree across candidates — see
   [dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md) P3 roadmap for
   the follow-up note.

Do not force items 2 and 3 into the same change as item 1 — land the widget against its
first real consumer, confirm it holds up, then adopt elsewhere.
