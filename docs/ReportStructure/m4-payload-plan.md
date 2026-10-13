# M4 — Payload: Implementation Plan

Follows [m3-coverage-plan.md](m3-coverage-plan.md). Tracked as milestone **M4** in
[ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path); the wire format is
[§ 14](ReportSystemVision.md#14-envelope-and-segmentation), the delivery budgets are
[§ 16.2](ReportSystemVision.md#162-delivery-side), and the table contract is
[§ 10.2](ReportSystemVision.md#102-rendering-large-sets-without-capping). Maps to
[ReportFormatCleanSlate](ReportFormatCleanSlate.md) **P5**. Depends on nothing; M5 (query) depends on it.

**Status:** planned 2026-10-13, nothing built. Steps 1-2 done (measured, spiked); steps 3-10 pending. Decisions D1-D6 are settled below; open questions are listed at the end.

## What M4 does

Today the report is one 20.8 MB payload (2.8 MB compressed in the HTML) that is inflated and parsed in full before anything shows. It ships every table row (161,871 rows in 138 tables, 68,576 in the largest) and builds a DOM node for each; opening every section puts about 2 million nodes in the page.

M4 changes three things:

1. **Split the payload.** A small `core` (verdict, claims, board) loads first; tables, entity data and section blocks are lazy segments inflated on demand.
2. **Draw tables from column data.** A table is a column store; the viewer draws 50-row pages from it and runs sort, filter and aggregates over whole columns, not DOM nodes.
3. **Stop dropping rows in the report.** Remove the report-side row caps and the viewer's 20-row default; where an analyzer itself stops early, the report says "N of M shown".

M4 does not change what analyzers find, add queries (M5), or redesign the look.

## Goal

A reader gets the verdict fast, and a large table is genuinely navigable. Three visible changes:

1. First paint no longer waits for the whole payload to be inflated and parsed.
2. A table shows all of its rows (the vision's example is 68,576), drawn from column data, not one DOM node per row.
3. A table title states the true row count, bound to the data.

## Decisions

| # | Decision | Status |
|---|---|---|
| D1a | Viewer rendering: real paging from the column store (option A), or virtualized scroll (option B, vision § 10.2) | **Settled by the step 2 spike: option A.** Paging cleared every table gate by a wide margin. Option B costs row-height handling, no Ctrl+F on unrendered rows and extra accessibility and print work; revisit only if a real table misses a gate |
| D1b | Row caps on tables | **Layer 1 decided: remove** (list under step 8), except the ExecutiveSummary teasers. **Layer 2 deferred:** keep for M4, show "N of M shown" where the analyzer knows the total; each cap is its own later decision because removing it changes the analyzer and its memory cost. **Layer 3 out of scope** (scan bounds) |
| D2 | Pre-render path | **Delete entirely, no fallback.** Removes `PreRender`, `ReportHtmlShared`, the `{{PRE_RENDERED_*}}` template slots and the forced-pre-render branch in `HtmlReportRenderer` (vision and ReportFormatCleanSlate P0b) |
| D3 | Binary columns | **JSON arrays only.** Typed-array base64 per column only if a measurement shows it matters |
| D4 | Trend reports | **Single-dump first;** trend follows as step 10 |
| D5 | Observations sidecar | **Keep `report.observations.*` as is** |
| D6 | `entities`, `entitySummary`, `relations` (about 5.2 MB) | **Lazy segments.** They feed the dossier panel, so the core is verdict, claims and board |
| D7 | Where rows become columns | **Option A now: a pure rows-to-columns converter at the serialization boundary** (a `ColumnTable` model; builders, `EntityCellPass`, `ReportStringPool` and the Markdown formatter untouched). **Option B deferred, not dropped: builders emit columns directly** (see [Deferred work](#deferred-work-do-not-drop)) |
| D8 | Which output carries columns | **HTML payload first,** converter shared. `report.json` moves to columns later in M4 after its consumers are listed (see Deferred work); until then HTML and `report.json` differ in table shape, against § 13 rule 2 |

## Starting point (read from code 2026-10-13, then measured; see step 1)

| Piece | Where it is today | Gap against § 14 / § 10.2 |
|---|---|---|
| Embedding | One payload `{report, perDumpDocs}`; gzip + base64 above 200,000 bytes ([HtmlReportRenderer.cs](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs)) | `report.main.js` inflates and `JSON.parse`s all of it before drawing. No segments, no manifest |
| Tables | Row-major `CompactTable` of `CompactRow` arrays ([AnalyzerDetailSection.cs](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs)). `RowLimit` (default 20) is viewer paging only: `report.renderers.sections.js` builds a DOM row for every shipped row and sets rows past the limit `hidden`; lazy hydration above 180 rows | No column store, stats or per-column typing; every shipped row is a DOM node |
| String dedup | `ReportStringPool`, `EntityCellPass`, `EventLeakSubscriberPool` applied on the non-pre-rendered path | String columns are already integer pool indexes (see step 2 finding) |
| Pre-render | `HtmlReportRenderer` forces it above 2 MB JSON or 1,000 findings | Cannot be segmented; deleted by D2 |
| Budgets | § 16.2 targets, set before anything was built | Measured in steps 1-2 |

## Steps

1. **Measure the baseline.** *(done 2026-10-13)*
   - **Run:** `dotnet run --project src/DumpDetective.Cli -c Release -- <3.35 GB reference dump> --output <dir>/report.html`; 82.7 s wall, peak working set 3.1 GB. Outputs in `D:\DUmps\Crash_IIS_BALTSTPRD\m4-step1\before\`.
   - **Sizes:** `report.html` 3.3 MB (CSS 161 KB, JS 342 KB, payload 2.8 MB gzip+base64); payload inflated 20.8 MB; `report.report.json` 93.8 MB (larger than the HTML payload: no string pool or compact rows); observations sidecar 7.5 MB gz.
   - **Payload by key (inflated, compact):** `domains` 15.0 MB, `entitySummary` 2.4 MB, `entities` 2.3 MB, `relations` 0.5 MB.
   - **Tables:** 138 tables, 161,871 rows, every row shipped; 47 tables over 20 rows, 20 over 180. Largest: finalizer queue 68,576 (the full population, not capped by `queueSamples`); 13,884 each for leak candidates, per-type generation profiles and top types; 13,457 duplicate strings; 10,939 Gen2-retained types.
   - **Browser (headless Chrome over DevTools, `file://`):** load event 235 ms; first paint 92 ms (the empty shell, not the verdict); main element has content at about 1.7 s; 130,430 DOM nodes after load. JS heap after GC: 61.8 MB after load, 50.5 MB after opening all 4,818 `<details>`, with 2,071,908 DOM nodes. JS heap only; DOM native memory is not counted, so the ≤ 250 MB gate is still unmeasured as a process figure.
   - **Caveats:** one run, one machine, warm cache; "content ready" is a polled node-count heuristic (50 ms steps), not a verdict paint. The script (`measure.mjs`) is in the session scratchpad, not committed.
   - **Cap audit** (matched each shipped table to its cap in code; a row count equal to a cap suggests truncation, the true population was not measured):
     - **Layer 1, report-side, drops rows that exist:** TypeSystem "Type table" ships 30 of the 13,884 types that Memory "Top types" ships in full; Crash "Crash buckets" and "Exception instances" ship exactly 25 (cap 25); Crash "Exception type counts" and "Exception heap size by type" ship exactly 15 (cap 15). Crash retention paths (7 of 25) and thread candidates (1 of 5) are under their caps.
     - **Layer 2, analyzer-side, ships exactly at the cap:** four Dominator tables (15); "Top pinned handle addresses" (25); "Top frame hotspots (cross-cluster)" (10); event-leak "Top subscriber types" and "Top handler methods" (20) and "Publisher events by subscriber count" (50); "Types by string field ownership" (100).
     - **Layer 3, scan bounds (out of scope):** `StringAnalyzer` owner-type and sample limits, `MaxLengthSamples`, `ReferenceGraph.MaxCachedNodes`, `LargeObjectTracker.MaxEntries`.
     - **Under their caps:** Collections "Wasteful collections" 2 of 50; Hang "Top continuation types" 2 of 5; "Top large objects by size" 97 (cap 100 in `LargeObjectTracker`).
     - **No matching table found:** Hang "top waiting threads" (cap 10) and the thread-cluster signature and cluster caps (5, 12); they may render as blocks.
     - **Net:** few real Layer 1 removals (Type table, Crash buckets and instances, Crash exception types); most other capped tables are Layer 2.

2. **Spike the envelope.** *(done 2026-10-13; the machinery works, the core budget needs more than table segmentation)*
   - **Built (scratchpad only, not committed):** `build_spike.py` empties the finalizer table's rows from the step 1 payload and writes them as a column store (hex addresses as numbers, null cells as per-column reasons) in one gzip+base64 segment; `tpl.html` inflates the core, shows a one-line verdict, then lazy-inflates the segment and draws 50-row pages; `run_spike.mjs` drives it in headless Chrome; a second page holds the unsegmented payload.
   - **Finding, machinery is cheap:** the segment (68,576 rows, 4.6 MB inflated) inflates in about 23 ms and parses in 16 ms; first 50-row page 0.9 ms; jump to row 40,000 about 1 ms; sort of the full column 9-11 ms (typed-array index sort, main thread); a filter over the full column 1 ms (51,969 matches); DOM stays at 572 nodes.
   - **Finding, column-major layout is not smaller:** the string pool already turns `Type Name`, `Generation` and the Yes/No columns into integer indexes, so the table's rows gzip to 309 KB as shipped and the column store to 598 KB. Size comes from segmentation and lazy load, not from column-major encoding. Delta-encoding sorted numeric columns (the cache's v6 idiom) was not tested and is the lever to try if size matters.
   - **Finding, tables alone do not reach the core budget:** unsegmented payload 20.35 MB; finalizer table out 16.1 MB (time to verdict about 257 ms vs 346 ms in this bare page); all 20 tables over 180 rows out an estimated 11.3 MB, against a § 16.2 target of 2 MB core and 8 MB total. The remainder is not tables: `domains` section blocks 5.9 MB (memory 3.5 MB, leaks 2.1 MB), `entitySummary` 2.4 MB, `entities` 2.3 MB, `relations` 0.5 MB. Hence D6 and open question 1.
   - **Caveats:** one table, one machine, warm cache; the page is a spike, so "verdict" is parse plus one heading and ignores real rendering.

3. **Column store model.** *(converter and model built 2026-10-13; not wired into any output yet)* A column-major table type per [§ 14.2](ReportSystemVision.md#142-column-store), built by a converter (D7) and emitted in the HTML payload (D8).
   - **Why keep it** (not for size; see step 2): per-column `kind`, `unit`, `semantics` and precomputed `stats` so a header shows its distribution without inflating the segment; M3's null reasons ("not measured", "ambiguous") as per-column `nulls`; sort, filter and aggregate over whole columns; the same struct-of-arrays idiom as the object index in [binary-format.md](../binary-format.md), and a place for delta encoding later.
   - **Converter:** runs after `EntityCellPass` and `ReportStringPool`; keeps existing pool indexes as-is (re-encoding saves nothing); lifts `{"u": ...}` cells into per-column `nulls` with reasons; computes `min`, `p50`, `p99`, `max`, `sum` for numeric columns only.
   - **Tests:** converter unit tests (unknown-cell mapping, empty columns, mixed-type columns) and a columns-to-rows round trip.
   - **Version:** bump `SdkVersion.Minor` and `schemaVersion` when `report.json` adopts columns (D8), not for the HTML-only change.
   - **Built:** `ColumnTable`, `ColumnTableColumn`, `ColumnStats` in `Reporting/Models/ColumnTable.cs`; `ColumnTableConverter.ToColumns` / `ToRows` in `Reporting/Services/`; 15 tests in `Unit/Reporting/ColumnTableConverterTests.cs` (all pass; only this class was run, not the full suite).
   - **Decisions taken while building:** stats come from the header type (`number`, `bytes`, `percent`, `int`, `long`, `double`) over the cells that are numbers, and carry a `Count`, because the 2026-10-13 payload has stray text in numeric columns (about 134 string cells in `number` columns, 30 in `bytes`) and pooled string columns hold ints; a column-wide null reason is stated once only when no plain null shares the column, otherwise each unknown row is mapped; a row with more cells than headers throws instead of dropping cells, which is safe only because nothing calls it yet.
   - **Not done:** no JSON converter or `ReportJsonContext` entry for `ColumnTable`, no call site, and the viewer still reads rows, so the HTML payload is unchanged (wiring is step 4-6). Not run against the real 3.35 GB payload; the first real-data check should be that every one of the 138 tables converts without the over-wide-row exception and round-trips.
   - **Finding for later:** numeric-declared columns holding stray text cannot become typed arrays (D3, delta encoding) until those cells are fixed or moved out; a follow-up to list which builders emit them.

4. **Segmenter.** *(pending)* Split the document into `core` plus lazy segments with a manifest of ids and sizes.
   - **Segments:** large tables, entity data (D6), and section blocks (open question 1).
   - **Boundary rule (§ 14.1):** everything a claim cites is in `core` or in a segment that claim names.
   - **Entity ids:** claims and table cells must resolve an entity before its segment loads (open question 2).

5. **Encoder.** *(pending)* Plain JSON below the existing threshold, gzip + base64 JSON arrays above it, per segment (D3). Determinism (§ 15): stable ordering, fixed float formatting, byte-identical output apart from timestamps.

6. **Viewer.** *(pending)* Lazy-inflate segments on demand through `DecompressionStream`; announce large inflates from manifest sizes; draw tables in 50-row pages from the column store (D1a); sort and filter over the full column, with the result count always true. Move to a worker only if the main-thread sort (9-11 ms in the spike) misses the 200 ms gate on a bigger table.

7. **Honest titles and export.** *(pending)* Generated title ("All 68,576 …, sorted by …") bound to the row count; CSV/JSON export from the same column store.

8. **Remove caps and pre-render.** *(pending)*
   - Remove `RowLimit` paging in the viewer.
   - Remove the Layer 1 caps (D1b): TypeSystem `TopRows`, Crash buckets, instances and exception-type tables. Keep the ExecutiveSummary teasers.
   - Add "N of M shown" to Layer 2 tables wherever the analyzer knows the total.
   - Delete the pre-render path (D2).

9. **Verify.** *(pending)* Run the § 16.2 gates on the reference dump and open the report in a browser (M3's strip and panel were never looked at in one). Measure process memory, not only JS heap. Add golden and size tests built from the real payload, not a synthetic fixture (131,568 distinct type names will not reproduce synthetically).

10. **Trend reports.** *(pending, D4)* Segment `perDumpDocs` and trend sections.

## Deferred work (do not drop)

- **Builders emit columns directly (D7 option B).** Better long-term: removes the build-rows-then-convert step and fits § 16.1 (the report builder may not hold large tables in memory just to reshape them). Costs touching about 45 section builders (69 `CompactTable` call sites) plus `EntityCellPass`, `ReportStringPool` and the Markdown formatter. The spike did not show it is needed; do it builder by builder after the converter is proven, largest tables first.
- **`report.json` carries columns (D8).** Needs a consumer list first (golden tests, trend baseline, external scripts) and the `schemaVersion` bump; tracked here so the HTML/`report.json` difference does not become permanent.

## Open questions

1. **Section blocks.** `domains` blocks are 5.9 MB inflated even with tables out. Per-section segments, or part of `core`? Without moving them the core cannot approach 2 MB.
2. **Entity id resolution.** A small id-to-name index in `core` with the rest lazy is one option; not designed yet.
3. **Layer 2 caps.** Each stays for M4; whether any is removed is a later per-cap decision with its memory cost measured.
4. **Delta encoding.** Whether delta-encoding sorted numeric columns shrinks the segments enough to matter (D3 defers binary columns until a measurement says so).

## Constraints

- The report builder may not materialize the column store in memory to write it, nor `.ToList()` an observation stream to sort it (§ 16.1).
- Real-dump runs are one at a time, in the foreground; a run silent past 5 minutes is hung (see [CLAUDE.md](../../CLAUDE.md)).
- No content in the HTML that is not in `report.json` (§ 13 rule 2); `report.json` stays complete and canonical.
- Markdown and text renderers must keep printing full tables or say how many rows they print; they must not silently drop rows when the model changes.

## Not in M4

- Filter chips and `dd.query()` (M5).
- Tokenized CSS and minified bundle gates (ReportTemplateCleanSlate T0-T5); the CSS/JS size gates in § 16.2 are measured here but not owned here.

## Risks

- **Browser heap.** The ≤ 250 MB target after full drill-down is unmeasured as a process figure; step 9 owns it.
- **Pre-render removal (D2)** can break a consumer we do not know of (email, SharePoint preview, PDF pipeline) that relied on its no-JS view.
- **Markdown/text row counts.** Removing the caps changes what those formatters receive.
