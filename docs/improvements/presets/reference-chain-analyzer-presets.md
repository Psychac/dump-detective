# ReferenceChainAnalyzer — Presets

Purpose: build reference chains and retainer summaries for suspect objects using bounded search strategies.

Where to look in the repo:
- Analyzer: src/DumpDetective.Analysis/Analyzers/ReferenceChainAnalyzer.cs
- Options: src/DumpDetective.Core/Options/ReferenceChainOptions.cs

Key knobs (from `ReferenceChainOptions`):
- `TopCount` — how many findings to trace in depth.
- `MaxPathDepth` / `FastModeMaxDepth` — BFS depth caps.
- `MaxPathSearchObjects` — max BFS nodes in Fast mode.
- `SearchMode` — `Fast` / `Balanced` / `Deep` (resolves to per-mode candidate/node/depth defaults).

Built-in presets (concrete values from `ReferenceChainOptions.Preset`):
- **Fast:** `TopCount=5`, `MaxPathDepth=12`, `FastModeMaxDepth=12`, `MaxPathSearchObjects=2_000`, `SearchMode=Fast`, `MaxCandidateNodes=10_000`, `MaxCandidateDepth=6`, `MaxRootExpansionDepth=8`, `SkipArrays=true`.
- **Balanced (default):** class defaults (`TopCount=5`, `MaxPathDepth=25`, `FastModeMaxDepth=25`, `MaxPathSearchObjects=5_000`, `SearchMode=Balanced`) which resolve to `MaxCandidateNodes≈50_000`, `MaxCandidateDepth≈8`, `MaxRootExpansionDepth≈12`.
- **Full:** `TopCount=20`, `MaxPathDepth=40`, `FastModeMaxDepth=40`, `MaxPathSearchObjects=20_000`, `SearchMode=Deep`, `MaxCandidateNodes=200_000`, `MaxCandidateDepth=15`, `MaxRootExpansionDepth=25`, `SkipArrays=false`.

Rationale — when to pick each preset:
- **Fast:** use for quick triage; Fast mode uses shallow forward BFS and small candidate sets to avoid large memory/CPU footprints.
- **Balanced:** default trade-off between accuracy and resource use; bidirectional candidate selection reduces false negatives while bounding work.
- **Full:** largest candidate set and deepest expansions to find long/rare root paths; use only for targeted investigations on capable hosts.

Flow note:
- `MaxCandidateNodes`, `MaxCandidateDepth`, and `MaxRootExpansionDepth` are resolved per `SearchMode` if left zero; tune them only when you need deterministic budgets.
