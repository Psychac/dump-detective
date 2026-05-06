# WeakReferenceAnalyzer — Presets

Purpose: show weak-reference retention and referent lifetimes.

Options:
- `TopWeakRefs`, `IncludeReferentLifetimes`, `ProduceRawExports`


Built-in presets (from `WeakReferenceAnalysisOptions.Preset`):
- **Fast:** `HandleScanCap = 20_000`, `TopTypeLimit = 8`.
- **Balanced (default):** class defaults: `HandleScanCap = 50_000`, `TopTypeLimit = 15`.
- **Full:** `HandleScanCap = 200_000`, `TopTypeLimit = 40`.

Rationale:
- **Fast:** small handle scan cap keeps analysis quick on dumps with many handles.
- **Balanced:** default caps balance coverage and cost.
- **Full:** larger cap reduces chance of capping and surfaces more weak-reference types at the cost of more work.
