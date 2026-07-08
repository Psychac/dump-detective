# Analysis Snapshot Export — Plan & Design Notes

## Goal

Export a compact JSON after analysis so reports can be re-rendered from that file without re-loading or re-analyzing the dump. Useful for:
- Iterating on report styles/formats without re-running analysis (slow, requires the dump file)
- Archiving analysis results independent of the dump file
- Sharing findings without sharing the dump

---

## Where to Export (decided)

**After `BuildReportStage`** — serialize `AnalysisReportDocument` (or a leaner snapshot variant) directly via `ReportJsonContext`.

Why not earlier:
- After `RunAnalyzersPipelineStage`: `AnalyzerRunResult` contains polymorphic `AnalyzerDomainResult` subtypes with no serialization contract — would require significant work to round-trip.
- After `WriteOutputStage`: the current `SeparateJson` flag already does this, but via fragile regex extraction from the rendered HTML — not from the model directly.

Why `BuildReportStage` is right:
- `AnalysisReportDocument` already has full `System.Text.Json` source-gen coverage via `ReportJsonContext`.
- Re-render from it is one call: `reportBuilderFacade.RenderDocument(doc, format, settings)`.
- `BuildReportStage` already populates `state.ReportDocument` — serialize it directly, no HTML involved.

---

## Compact JSON Design (analysis-only variant)

### Problem with current `AnalysisReportDocument` as snapshot

The current document is designed as a **renderer input** — it carries both structured analysis data AND pre-rendered narrative `SectionBlock[]` arrays per section. `SectionBlock[]` is the biggest bloat:
- `TextBlock`, `ListItemBlock`, `HeadingBlock`, `PathBlock`, `StackFrameBlock`, `TableBlock`, `CollapsibleSectionBeginBlock/End`, `ChartBlock`, `SparklineBlock` — full prose for every analyzer section
- Sections like Thread Analysis (stack frames), Leak Detection (reference paths), and Memory Analysis can each have hundreds to thousands of blocks
- These are **display artifacts**, not raw analysis data — they carry no additional structured information beyond `KeyMetrics`, `CompactTables`, and `LeadFinding`

Additionally, the document carries renderer hints (`renderMode`, `reportStyleVersion`) that are meaningless for an analysis snapshot.

### Where `Blocks` are consumed (renderer impact)

Blocks are read in **three places**:
1. **`HtmlReportRenderer`** — serializes the whole document to JSON; the embedded JS renderer (`blocks.js`) reads `blocks[]` from the JSON to populate the collapsible detail panel of each section
2. **`ReportHtmlShared.RenderAnalyzerSections`** — the server pre-render path (triggered by `PreRender: true` or when JSON > 2 MB), calls `RenderBlocksHtml(section.Blocks, sb)` to generate static HTML inside each section card
3. **`MarkdownCanonicalReportFormatter`** — calls `RenderBlocksMd(section.Blocks, sb)` for the `AnalyzerSections` render path

**What is NOT in `Blocks`:** the always-visible strip — `LeadFinding`, `KeyMetrics`, `CompactTables` — these are structured slots rendered independently by the JS renderer. They survive intact even when `Blocks` is empty.

**Impact of empty `Blocks` on rendered output:**
- ✅ Health scorecard, executive summary, findings list — unaffected (sourced from document root)
- ✅ Per-section lead finding card (severity badge, title, recommendation, confidence) — unaffected (`LeadFinding`)
- ✅ Per-section key metrics strip — unaffected (`KeyMetrics`)
- ✅ Per-section data tables — unaffected (`CompactTables`)
- ❌ Per-section collapsible detail panels — empty (this is where prose narrative, stack frame chains, reference paths, and inline metric text live)

So the snapshot re-render produces a fully functional report for triage and decision-making, but loses the "expand for details" narrative content per section.

### Two snapshot fidelity modes

Rather than a single choice, support two modes:

| Mode | `Blocks` included | File size | Re-render fidelity |
|---|---|---|---|
| `compact` | No | ~60–80% smaller | Missing collapsible narrative prose |
| `full` | Yes | Same as current JSON | Exact re-render, identical to original |

`compact` is the default for the cache use case (fast skip of re-analysis). `full` is for archiving exact report state or sharing.

### Renderer adaptation for `compact` snapshots

When `Blocks` is empty or null in a section, the three rendering sites need to handle it gracefully:
- **JS renderer (`blocks.js`)**: already skips empty arrays — the collapsible detail toggle should be hidden/disabled when `blocks` is empty rather than showing an empty panel. Requires a small JS guard.
- **`ReportHtmlShared.RenderAnalyzerSections`**: `RenderBlocksHtml` iterates blocks — empty array renders nothing, which is already correct. May want to suppress the collapsible wrapper element entirely.
- **`MarkdownCanonicalReportFormatter`**: `RenderBlocksMd` iterates blocks — empty array renders nothing. Already correct.

Net change needed in renderers: minimal. Primarily a UX polish to hide the expand toggle when there's nothing to expand.

### Proposed: `AnalysisSnapshotDocument`

A new model with structured slots only (no `Blocks`). Stored in `DumpDetective.Reporting.Models` alongside `AnalysisReportDocument`.

```
AnalysisSnapshotDocument
├── schemaVersion           "snapshot/1.0"
├── fidelity                "compact" | "full"
├── dumpFingerprint         ← hex of partial hash (used for cache matching)
├── generatedAtUtc
├── analyzerVersion
├── incidentContext         ← AnalysisIncidentContext (dump path, CLR version, etc.)
├── healthScorecard         ← HealthScorecard
├── executiveSummary        ← ExecutiveSummaryRecord
├── crossDomainInsights[]   ← FindingRecord[]
├── correlationEvents[]     ← CorrelationEventRecord[]
└── domains[]
    └── SnapshotDomainSection
        ├── domain
        ├── leadSeverity
        └── sections[]
            └── SnapshotAnalyzerSection
                ├── analyzerName
                ├── sectionId
                ├── sortOrder
                ├── leadFinding     ← SectionLeadFinding
                ├── keyMetrics      ← map: snake_case → MetricValue
                ├── compactTables[] ← CompactTable[]
                ├── provenance      ← SectionProvenance
                └── blocks[]        ← SectionBlock[] — populated in "full" mode, empty in "compact"
```

Reuses existing model types (`FindingRecord`, `CompactTable`, `MetricValue`, `SectionLeadFinding`, etc.) — no duplication.

**What's dropped vs. `AnalysisReportDocument`:**
- `renderMode`, `reportStyleVersion` — renderer-specific hints, not needed in snapshot
- `Appendix.MemoryDiagnostics` — diagnostics-mode only; keep `AnalyzerRunSummary` for provenance, drop memory stats

**Estimated size reduction (compact mode):**

| Section type | Reduction |
|---|---|
| Thread / Async (stack frames as `StackFrameBlock`) | ~70–85% |
| Leak / Memory (reference paths as `PathBlock`) | ~60–75% |
| GC / Type System (inline text + `TableBlock`) | ~40–60% |
| Modules / AppDomain (mostly tables already) | ~10–20% |

Average dump report JSON of ~2–4 MB → ~400–900 KB in compact mode.

---

## Default-On JSON Export with Cache Skip

### Goal

Make snapshot export **the default on every run**. Before analysis, check if a matching snapshot already exists for the same dump. If it does, skip the entire analysis pipeline and go straight to rendering.

### Dump Fingerprint

Hashing a full 10–25 GB dump is not feasible. Use a **partial hash** instead:
- Read the first 4 MB + last 4 MB of the dump file
- Combine with `FileInfo.Length`
- SHA-256 → truncate to 16 bytes (32 hex chars)

This is fast (<1s even for large files), collision-resistant enough for a local cache, and detects file changes (unlike mtime alone, which is fragile on file copies/moves).

```
fingerprint = SHA256(first_4MB_bytes + last_4MB_bytes + little_endian_file_size_bytes)[0..16]
             → 32-char hex string
```

### Cache Workflow

```
CLI start
    │
    ▼
ComputeDumpFingerprint(dumpPath)           ← fast partial hash, ~subsecond
    │
    ▼
LookupSnapshot(fingerprint, analyzerVersion)
    │
    ├── HIT: snapshot exists, fingerprint + analyzerVersion match
    │         │
    │         ▼
    │       LoadSnapshotStage              ← deserialize JSON → AnalysisSnapshotDocument
    │         │
    │         ▼
    │       SnapshotToReportDocumentAdapter ← reconstruct AnalysisReportDocument (Blocks empty in compact)
    │         │
    │         ▼
    │       BuildReportStage (from adapted doc)
    │         │
    │         ▼
    │       WriteOutputStage               ← no new snapshot written (already exists)
    │
    └── MISS: no snapshot or stale
              │
              ▼
            Full pipeline (LoadDump → BuildHeapIndex → RunAnalyzers → BuildReport)
              │
              ▼
            SaveSnapshotStage              ← serialize AnalysisSnapshotDocument to cache path
              │
              ▼
            WriteOutputStage
```

### Cache Location

Default: alongside the dump file as `<dumpname>.<fingerprint_prefix8>.snap.json`
- Example: `app.dmp` → `app.a3f7c291.snap.json`
- Configurable via `--snapshot-dir <path>` to redirect to a shared cache directory
- `--no-cache` to bypass lookup and skip save (for CI/one-shot scenarios)
- `--force-reanalyze` (or `--no-cache-read`) to ignore existing snapshot but still save a new one

### Cache Invalidation

A snapshot is considered **stale** and discarded when:
1. Fingerprint mismatch (dump file changed)
2. `schemaVersion` in snapshot doesn't match current expected version
3. `analyzerVersion` in snapshot differs from current binary version (configurable: `strict` invalidates, `warn` uses it with a console warning)
4. Explicit `--force-reanalyze` flag

### Pipeline Changes

The `StagedPipelineRunner` already runs stages sequentially and `SingleDumpPipelineState` is the shared state bag. The cache check fits as a **pre-pipeline decision** in the CLI command handler, before `StagedPipelineRunner` is invoked:

```csharp
// In AnalyzeCommand or ExecutePerDumpPipelineStage:
var fingerprint = await DumpFingerprintComputer.ComputeAsync(dumpPath, cancellationToken);
var snapshot = snapshotCache.TryLoad(fingerprint, analyzerVersion);
IReadOnlyList<IAnalysisStage> stages = snapshot is not null
    ? BuildSnapshotRenderPipeline(snapshot)   // 3 stages: LoadSnapshot → BuildReport → WriteOutput
    : BuildFullAnalysisPipeline();            // 5 stages: Load → Index → Analyze → BuildReport → WriteOutput + SaveSnapshot
```

This keeps `StagedPipelineRunner` untouched — it just gets a different stage list.

---

## Re-render Pipeline (standalone)

For the case where someone wants to re-render from an existing snapshot without a dump present:

```
analyze render --from-snapshot <path>.snap.json --output report.html --format html --style v2
```

```
LoadSnapshotStage
    → SnapshotToReportDocumentAdapter
    → BuildReportStage (format + style overridable)
    → WriteOutputStage
```

---

## TODO

### Models & Serialization
- [ ] Design `AnalysisSnapshotDocument`, `SnapshotDomainSection`, `SnapshotAnalyzerSection` in `DumpDetective.Reporting.Models`
- [ ] Create `SnapshotJsonContext` (source-gen) — reuse existing slot types where possible, avoid duplication
- [ ] Schema-version the snapshot model (`snapshot/1.0`)

### Fingerprinting
- [ ] Add `DumpFingerprintComputer` in `DumpDetective.Analysis` — partial hash (first 4MB + last 4MB + file size), returns 32-char hex
- [ ] Store fingerprint in `AnalysisSnapshotDocument.dumpFingerprint`

### Cache Infrastructure
- [ ] Add `ISnapshotCache` / `SnapshotCache` in `DumpDetective.Analysis` or `DumpDetective.Cli`
  - `TryLoad(fingerprint, analyzerVersion) → AnalysisSnapshotDocument?`
  - `Save(snapshot, cachePath)`
  - `ResolveCachePath(dumpPath, fingerprint, snapshotDir?) → string`
- [ ] Implement cache invalidation rules (fingerprint + schema + analyzerVersion)

### Pipeline wiring
- [ ] `SnapshotToReportDocumentAdapter` (Reporting) — reconstructs `AnalysisReportDocument` from `AnalysisSnapshotDocument`; `Blocks` empty in compact mode
- [ ] `LoadSnapshotStage` — deserialize snapshot, populate `state.ReportDocument` via adapter
- [ ] `SaveSnapshotStage` — after `BuildReportStage`, serialize full pipeline result to snapshot; inserted only in full-analysis pipeline
- [ ] CLI pre-pipeline decision: compute fingerprint → lookup → select stage list
- [ ] Add `--snapshot-dir`, `--no-cache`, `--force-reanalyze` options to `ResolvedExecutionOptions`
- [ ] `--fidelity compact|full` option (default `compact`)

### Renderer adaptation
- [ ] JS renderer (`blocks.js`): hide the section expand toggle when `blocks` is empty/null
- [ ] `ReportHtmlShared.RenderAnalyzerSections`: suppress collapsible wrapper element when section has no blocks (pre-render path)
- [ ] Markdown formatter: no change needed (empty blocks already renders nothing)

### Standalone re-render command
- [ ] Add `render` sub-command to CLI: `--from-snapshot`, `--output`, `--format`, `--style`

### Tests
- [ ] `DumpFingerprintComputer`: same file → same fingerprint; modified file → different fingerprint
- [ ] `SnapshotCache`: save/load round-trip; stale detection
- [ ] End-to-end: full analysis → save snapshot → render from snapshot → scorecard, findings, tables match original
- [ ] Compact mode: collapsible detail sections absent; all structured slots present
