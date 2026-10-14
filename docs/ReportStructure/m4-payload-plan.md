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
| D9 | Section blocks (was open question 1) | **Every section's typed slots are lazy by default, no size threshold.** Measured 2026-10-14: narrative `blocks` are 14 KB, so they stay in `core`; the bulk is typed slots, 5.5 MB in two sections (A5 GC roots 3.5 MB: owned-subgraph groups; D4 event leaks 2.1 MB: instance cards, group cards, subscriber pool). Interpretation to confirm: "lazy by default" read as all typed slots, not only large ones |
| D10 | Entity ids before their segment loads (was open question 2) | **No id-to-name index in `core`.** `entities` is one lazy segment, inflated before the first entity-bearing table is drawn or a dossier opens; `entitySummary` and `relations` load when a dossier opens. Core claims reach entities through `entitySummary.claimIds`, not the reverse. Verified 2026-10-14 by grep: header, panels, findings, nav and blocks renderers do not read the entity table; only entity cells in section tables (`report.renderers.shared.js`), the entity panel, and the table export in `report.ui.actions.js` do |
| D11 | Viewer behaviour over lazy content (decided 2026-10-14) | **Search:** global search covers the core text and every section already opened, and says how many sections were not searched ("N of M sections not opened"); a **"search everything" action that loads all segments is built up front, not later** (about 250 ms of inflate and parse measured). **Sections:** a section's segment loads on first open of its `<details>`, with a placeholder; a URL hash into a section (for example `#A5`) opens it and loads its segment before scrolling. **Tables:** the "N rows" summary comes from the stub's `rowCount`; rows load on first open. Tables of 180 rows or fewer keep the existing DOM paging and filter code; larger tables use a new column-based path (D1a), and the two are unified later only if it pays. **Entities:** `e-entities` loads before the first entity-bearing table renders, `e-dossier` on first dossier open, and the table export waits for `e-entities`. **Failures:** a segment that fails to load shows an inline error naming its id and leaves the rest of the page working |

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
   - **Finding, tables alone do not reach the core budget:** unsegmented payload 20.35 MB; finalizer table out 16.1 MB (time to verdict about 257 ms vs 346 ms in this bare page); all 20 tables over 180 rows out an estimated 11.3 MB, against a § 16.2 target of 2 MB core and 8 MB total. The remainder is not tables: `domains` section blocks 5.9 MB (memory 3.5 MB, leaks 2.1 MB), `entitySummary` 2.4 MB, `entities` 2.3 MB, `relations` 0.5 MB. Hence D6 and D9.
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

4. **Segmenter.** *(built 2026-10-14 as a pure component; not wired; checked on the real reference dump)* Split the document into `core` plus lazy segments with a manifest of ids and sizes.
   - **Segments:** large tables (over 180 rows, the viewer's lazy-hydration threshold; to confirm), every section's typed slots (D9), and entity data (D6, D10). Estimated `core` by subtraction: about 0.6 MB of 20.35 MB; not built.
   - **Boundary rule (§ 14.1):** everything a claim cites is in `core` or in a segment that claim names.
   - **Entity ids:** per D10, `entities` is inflated before the first entity-bearing table or dossier renders.
   - **Built:** `ReportSegmenter.Segment` (`Reporting/Services/`) returns `SegmentedReport(Core, Segments)`; `ReportSegment`, `SectionSegmentPayload`, `EntityDossierPayload` (`Models/ReportSegment.cs`); `ColumnTableJsonConverter` (write-only); `CompactCellJson` extracted from `CompactRowJsonConverter` so a cell has the same bytes in a row and in a column; `SegmentId` on `AnalyzerDetailSection`, `RowCount` and `SegmentId` on `CompactTable` (null unless segmented, so `report.json` is unchanged). 13 tests in `Unit/Reporting/ReportSegmenterTests.cs`; 344 reporting-area tests pass.
   - **Segments produced:** `s-<sectionId>` per section with any typed slot or table; `t-<sectionId>-<tableIndex>` per table over 180 rows (a `ColumnTable`, replaced by a stub with `RowCount` in its section segment); `e-entities`; `e-dossier` (summary and relations). A repeated id gets `~2`, `~3`. Sections with nothing lazy, and narrative blocks, stay in the core. Trend reports return unchanged (D4).
   - **Not done:** no manifest yet (ids and sizes are on `ReportSegment`; the manifest is built by the encoder, step 5); nothing calls the segmenter, so the HTML is unchanged; the viewer cannot read segments (step 6). The sizes in this plan's estimates (core about 0.6 MB) are still arithmetic.
   - **Real-data check (2026-10-14, reference dump, one 83 s CLI run with a temporary stats hook in `HtmlReportRenderer`, since removed and not committed; results in `D:\DUmps\Crash_IIS_BALTSTPRD\m4-step4\stats.txt`):** segmenting took 443 ms and produced 57 segments with unique ids (35 section, 20 table, `e-entities`, `e-dossier`). **The core is 280,566 characters (0.28 MB) against 20.35 MB unsegmented**, under the § 16.2 target of 2 MB; the arithmetic estimate was 0.6 MB, so the real figure is better. The largest core piece is `domains` at 160 KB. Lazy segments in characters: sections 5.98 M (largest `s-A5` 3.69 M, `s-D4` 2.06 M), tables 8.82 M (largest `t-B6-3` finalizer queue 4.19 M, `t-A7-4` 1.31 M), entities 2.44 M, dossier 2.95 M. The largest single segment is 4.2 MB inflated.
   - **Not measured:** compressed segment sizes (step 5), the core's inflate and parse time, and the string pool (1,251 entries) is in the core. Peak working set for that run was 3.7 GB against 3.1 GB for the step 1 run, which includes the hook holding every segment's JSON in memory at once; the real encoder must stream or write segments one at a time (§ 16.1).
   - **Existing gap, found while trying a different check:** `CompactRowJsonConverter.Read` rejects the `[id,"name"]` entity-cell arrays, so a saved `report.json` cannot be loaded back into the model.
   - **Core still carries:** `strings` (the string pool), `domains[].domainInsights`, `crossDomainInsights`, executive summary and appendix; their size after segmentation is unmeasured.

5. **Encoder.** *(built 2026-10-14 as a pure component; not wired)* Plain JSON below the existing threshold, gzip + base64 JSON arrays above it, per segment (D3). Determinism (§ 15): stable ordering, fixed float formatting, byte-identical output apart from timestamps.

   - **Built:** `ReportSegmentEncoder.Encode(segments)` (`Reporting/Services/`) returns `EncodedSegmentSet(ManifestElement, SegmentElements)`: a `dd-manifest` element (per segment: id, kind, encoding, inflated `bytes`, `encodedBytes`) and one `dd-seg-<id>` element per segment with `data-encoding` `json` or `gzip-base64`. 10 tests in `Unit/Reporting/ReportSegmentEncoderTests.cs`, all pass.
   - **Decided with the user 2026-10-14:** core stays in `report-json` in its existing shape; segment at or below 200,000 bytes stays plain JSON, above it gzip+base64; plain JSON has `<` written as `<` so a string holding `</script>` cannot end the element (the single-blob embedding did not do this); wiring waits for step 6.
   - **Properties:** output depends only on the input segments (tested, same input gives equal output); segment ids are HTML-encoded in the element id and kept raw in the manifest; each segment is encoded and appended in turn, so no encoded copy outlives its iteration (the segments' JSON strings are still all in memory, and the final HTML is one string as before).
   - **Real-dump measurement (2026-10-14, reference dump, one 83 s CLI run with a temporary hook that was reverted and not committed; results in `D:\DUmps\Crash_IIS_BALTSTPRD\m4-step5\`; the page was timed in headless Chrome over `file://`, two runs):**
     - **Core:** 280,606 chars inflated, 77,556 chars as gzip+base64; inflate 11 ms, `JSON.parse` 2.6 ms.
     - **Segments:** 57 segments, 20.19 MB inflated in total, 3.41 MB encoded; 11 are gzip+base64 and 46 stay plain JSON (under 200 KB). Largest encoded: finalizer queue table 608 KB (from 4.19 MB), `t-A7-4` 501 KB (from 1.31 MB), dossier 379 KB, entities 220 KB, `s-A5` 194 KB (from 3.69 MB).
     - **Page:** the encoder's elements total 3.5 MB and the page loads in 167-173 ms with everything inline, against 3.33 MB and the whole 20.8 MB inflate and parse today. Inflating and parsing every segment in turn takes about 127 ms inflate and 117-185 ms parse in total, so opening everything costs roughly a quarter of a second, and any one segment costs 20-40 ms (the dossier's parse varied 46-112 ms between runs).
     - **Against § 16.2:** core 0.28 MB vs 2 MB, total inflated 20.5 MB vs 8 MB (not met: the sections, tables and entities are lazy but still total 20 MB), core inflate-plus-parse about 13 ms vs the 150 ms time-to-verdict target (that gate also needs rendering, not measured here).
     - **Cost:** segmenting took 1.8 s and encoding 3.7 s on this run (the segmenter measured 0.4 s the first time; one machine, so treat as order of magnitude), and peak working set was 3.4 GB against 3.1 GB for the step 1 run.
     - **Not measured:** real viewer rendering time (this page only inflates and parses); the `report.html` size once wired, which will be about the segment elements plus the viewer.
   - **Not done:** not wired into `HtmlReportRenderer`. When wired, at least six integration tests that parse `report-json` for the full report (`P0SmokeTests`, `HtmlRendererCssTests`, `ReportFlowIntegrationTests`) must be updated to read core plus segments.
6. **Viewer.** *(slice 1 of 4 done 2026-10-14: renderer and loader wired, still eager; slices 2-4 pending)* Lazy-inflate segments on demand through `DecompressionStream`; announce large inflates from manifest sizes; draw tables in 50-row pages from the column store (D1a); sort and filter over the full column, with the result count always true. Move to a worker only if the main-thread sort (9-11 ms in the spike) misses the 200 ms gate on a bigger table.

   - **Slices (agreed 2026-10-14):** 1 wire the renderer and loader; 2 lazy sections with deep links; 3 column-based large tables (paging, sort, filter); 4 search over lazy content, entities and export (D11).
   - **Slice 1 built:** `HtmlReportRenderer` emits the core in `report-json` plus the manifest and segment elements (template slot `{{SEGMENT_ELEMENTS}}`); pre-render and trend keep one payload. New `report.segments.js`: `decodeElementText`, `readSegmentManifest`, `loadSegment(id)` (one load per id, errors name the segment), `columnTableToRows`, `rehydrateSegments(doc)`. Bootstrap awaits `rehydrateSegments` before anything else, which loads every segment and puts it back, so the rest of the viewer is unchanged; it is still eager, so nothing is lazy yet. The JSON and CSV downloads now read `window.__REPORT__` (the rehydrated document) instead of re-parsing `report-json`, which no longer holds the whole report. 4 tests in `Unit/Reporting/HtmlSegmentWiringTests.cs`; 464 tests in the reporting, html, golden, integration and segment filters pass (66 skipped, real-dump).
   - **Real-dump check (reference dump, one CLI run, headless Chrome, `D:\DUmps\Crash_IIS_BALTSTPRD\m4-step6a\`):** the wired report renders the same as the step 1 report: 37 sections, 139 tables, 2,255 body rows, 1,890 entity links, 102 findings, no script errors; the element histograms differ only by the 58 new script elements (57 segments and the manifest) and 4 delta-colored cells (`TD.delta-negative` 63 vs 67, `TD.delta-positive` 9 vs 5). Those 4 come from run-to-run data, not the change: the two runs' source tables are identical except "Types by string field ownership" (a sampled scan), which differs in 10 of its 100 rows. Peak working set 3.0 GB; HTML 3.8 MB against 3.3 MB (the segment elements plus per-element markup; the plan's step 5 estimate was 3.5 MB).
   - **Not measured:** time to verdict for the wired page (the eager rehydrate waits for every segment, so it should be about the old cost plus roughly 250 ms; the 1.5 s vs 1.9 s "main has content" readings are single runs and not a comparison).
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

1. **Layer 2 caps.** Each stays for M4; whether any is removed is a later per-cap decision with its memory cost measured.
2. **Delta encoding.** Whether delta-encoding sorted numeric columns shrinks the segments enough to matter (D3 defers binary columns until a measurement says so).

## Constraints

- The report builder may not materialize the column store in memory to write it, nor `.ToList()` an observation stream to sort it (§ 16.1).
- Real-dump runs are one at a time, in the foreground; a run silent past 5 minutes is hung (see [CLAUDE.md](../../CLAUDE.md)).
- No content in the HTML that is not in `report.json` (§ 13 rule 2); `report.json` stays complete and canonical.
- Markdown and text renderers must keep printing full tables or say how many rows they print; they must not silently drop rows when the model changes.

## Not in M4

- Filter chips and `dd.query()` (M5).
- Tokenized CSS and minified bundle gates (ReportTemplateCleanSlate T0-T5); the CSS/JS size gates in § 16.2 are measured here but not owned here.

## Risks

- **Search and filters over lazy content.** `report.ui.search.js` and the filter bar work on the rendered DOM, so text in a section or table that has not been opened is not searched. Needs a decision in step 6 (search the segments, or say what was not searched).
- **Browser heap.** The ≤ 250 MB target after full drill-down is unmeasured as a process figure; step 9 owns it.
- **Pre-render removal (D2)** can break a consumer we do not know of (email, SharePoint preview, PDF pipeline) that relied on its no-JS view.
- **Markdown/text row counts.** Removing the caps changes what those formatters receive.
