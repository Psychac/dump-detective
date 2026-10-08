# M2 — Entities: Implementation Plan

Implements [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path) step **M2**
(maps to [ReportFormatCleanSlate § 3](ReportFormatCleanSlate.md#3-entities-and-pivot-views), P3). The
entity model is specified in [ReportSystemVision.md § 4](ReportSystemVision.md#4-the-entity-model) and
the dossier surface in [§ 9.3](ReportSystemVision.md#93-surface-3--dossier); this doc covers only
sequencing, what exists today, and decisions before starting.

**Status:** draft 2026-10-06, nothing built. D1 decided 2026-10-06 (revised 2026-10-07 to option B); D2 decided 2026-10-07; D1–D5 all decided by 2026-10-07.

## Why M2 next

- **Critical path.** M5 (query) needs M2 + M4; Phase 8's entity-pivot UI needs M2.
- **Inputs already exist.** M1 claims and Phase 5 observations already name entities (see *Starting point*).
- **A computed result has no report surface.** Per-thread retention is built and rendered nowhere.
- **Dedup is a side effect, not the motivation** (same stance as ReportFormatCleanSlate § 3).

## Scope

| In | Out (and where it goes) |
|---|---|
| Entity table in `report.json` and the HTML payload for every kind that already exists as an `EntityRef` (type, thread, module, object, method) | New kinds: GC root, handle, memory segment, AppDomain (wanted, see *Deferred work*); `stringValue`, `external` stay unscheduled |
| Type dossier and thread dossier surfaces | Module, object, method, root, handle, segment and AppDomain dossiers (wanted; same layout, see *Deferred work*) |
| Per-thread retained bytes emitted as observations | Series over time in the dossier — needs N>1 artifacts (§ 3) |
| `#entity/type/…` anchors ([§ 9.5](ReportSystemVision.md#95-citation-and-anchoring)) | Query grammar, filter chips — M5 |
| Entity-valued compact-table cells reference the entity table | Virtualized tables, segmented envelope — M4 |

## Starting point (read from code and docs 2026-10-06; items marked *verify* not yet checked)

| Fact | Where |
|---|---|
| `EntityRef` has five subtypes: `TypeRef`, `MethodRef`, `ModuleRef`, `ThreadRef`, `ObjectRef`; identity is `Kind` + `JoinKey`, fidelity excluded from equality | `Sdk/Identity/EntityRef.cs` |
| Observations carry `Subjects`; 43 emission sites pass `Subjects = []` (process-wide scalars) | [m1-claims-plan.md § Adjacent milestones](m1-claims-plan.md#adjacent-milestones--state-today) |
| `Assertion.subject` exists but is set on only 1 of 82 claims on the reference dump (measured, step 1); claims reach entities through `derivedFrom` | `Sdk/Synthesis` |
| HTML dedup is `ReportStringPool`: a frequency pool over every string cell of `CompactTable` rows, with no notion of what a string means | `Reporting/Formatters/ReportStringPool.cs` |
| `IThreadRetentionProvider.TryGetRetainedBytesForThread(osThreadId)` reaches `IHeapDominatorQuery` in the SDK; no report code consumes it | `Core/Abstractions`, `Sdk/Analysis/IHeapDominatorQuery.cs` |
| `ThreadAnalyzer` report-surface wiring for retention was deliberately deferred | memory: Per-thread retention (§12.2) |
| HTML carries a slim claim projection with no observation ids; per-observation drill lives in `report.json` and the side-car | [m1-claims-plan.md step 5](m1-claims-plan.md) |
| Side-car holds 97,408 observations on the reference dump (6.0 MB gzipped, ~105 MB plain) | [m1-claims-plan.md step 2](m1-claims-plan.md) |
| Measured baseline: 131,568 unique type names across 1,022,984 cells (~9.9 MB), 71,944 hop strings | [ReportFormatCleanSlate Appendix A](ReportFormatCleanSlate.md#appendix-a--measured-baseline) |

## Decisions before step 1

| # | Decision | Recommendation |
|---|---|---|
| D1 | Do compact-table cells reference the entity table, or does `ReportStringPool` stay and the entity table sit beside it? | **Decided 2026-10-06 as "A, then maybe B"; revised 2026-10-07 to B.** Entity-typed columns: sections, compact tables, claims and observations reference the entity table by id, so the dossier is a lookup and not a second copy. `CompactHeader` already has a per-column `Type`, so no new typing mechanism is needed. `ReportStringPool` stays for non-entity strings (booleans, categories) and skips entity columns. |
| D2 | What does the HTML embed for a dossier? Vision § 5.5 says: observations cited by any claim, plus per-entity headline measures. | **Reframed 2026-10-07: index first, copy only what no table holds.** With D1 = B the dossier is mostly references into existing tables, claims and observations. Only facts with no table row get a small projection, selected by observation type (no top-N). The full ~105 MB store is not embedded; the dossier says it shows a projection and points at the side-car. Step 1 measures which dossier panels existing tables already cover. **Signed off 2026-10-07.** **Uneven spread (measured, step 1):** 12,345 of 14,233 types have exactly one observation, but `System.Data.DataColumn` has 54,009 (over half of all 97,408). A dossier therefore shows grouped totals (count per observation type, summed measures) for heavy entities and leaves the raw rows in the side-car; this is grouping, not a top-N cap, and the dossier states what it is showing. |
| D3 | Where does per-thread retained bytes come from? | **Decided 2026-10-07: `ThreadAnalyzer` emits a `thread.retained-bytes` observation per thread, with a `ThreadRef` subject.** The report must not call `IThreadRetentionProvider` at render time: the dump is not open then. Raw measure only, no threshold or flag. When the value is unavailable (no Stage B, or the thread had no stack roots) no observation is emitted, never 0, so the dossier shows "not measured". Rejected: `DominatorAnalyzer` (does not enumerate threads), a new analyzer (one measure does not justify it). |
| D4 | Entity ids: dense per-report integers, or the `JoinKey`? | **Decided 2026-10-07: dense integers in table cells, `JoinKey` stored on each entity and used in anchors.** **Determinism requirement:** two runs on the same input must produce identical output. Ids are assigned after all entities are collected, by sorting on kind then `JoinKey` (ordinal), never in discovery order, because analyzers can run in parallel and finish in a different order each run. |
| D5 | Does the dossier need claims to carry `relations` (retains / retained-by / subscribes-to)? | **Decided 2026-10-07: not in M2, deferred, but wanted.** The dossier ships without a Relations panel and shows it as not built yet. Not faked from root-chain text. Revisit as its own piece of work once claims gain `relations` (deferred in M1); the dossier layout leaves a slot for it. |

## Steps

1. **Measure before building** *(done 2026-10-07)* — read from yesterday's `report.json` and side-car for the
   3.35 GB reference dump; no dump loaded. Script and full output: `m2-step1/` beside the dump
   (`measure.py`, `m2-step1-results.txt`).
   - **Observations:** 97,408; 63 have no subject (process-wide scalars). Subject references: 96,929 type,
     339 module, 77 thread, 55 object, 7 method. Distinct entities: 14,233 types, 255 modules, 38 threads,
     55 objects, 7 methods.
   - **Type observations are dominated by two sources:** `finalizer.queue-entry` (68,576) and
     `leak.candidate-type` (13,884). Distinct types are 14,233 though, so the entity table stays small.
   - **Claims:** 82 of 82 carry an assertion. **Only 1 of 82 has `assertion.subject` set (a module);
     81 are null.** Entities are reachable through lineage instead: 40 of 82 claims cite observations
     that name at least one entity (2,470 type references). The other 42 cite only process-wide
     scalars. Plan impact: claim-to-entity links come from `derivedFrom`, not `subject`.
   - **Tables:** 138 compact tables, 957,723 string cells (15.1 MB literal). **152,394 cells (8.4 MB
     literal) are exact known type names**, spread over 13,345 distinct names, 13,021 of which repeat.
     So about 56% of string-cell bytes are entity-valued, enough to justify D1 option B.
   - **Table type columns are concentrated:** the top ones are finalizer queue (67,858 cells), Memory,
     GC Generation and Leak Candidate "Type" (13,011 each), Object Shape (10,066), Allocation Pattern
     (9,186 + 3,704). About 10 tables hold most cells, so step 3 can start with those.
   - **Threads:** 38 `ThreadRef`s; the emitted ones carry only `osThreadId` (the type has an optional managed id, unused). The retention
     provider keys on OS id, so D3 lines up. Closes the "thread identity" question below.
   - **Fidelity:** type refs are 95,249 Exact, 1,455 Low, 225 High; all thread/module/object refs Exact.
   - **Spread per type:** finalizer queue entries are 68,576 observations over 143 types, weak-handle
     targets 8,196 over 58, leak candidates 13,884 over 13,491. Median type has 1 observation; the
     maximum is 54,009 (`DataColumn`). Script: `m2-step1/per-type-spread.py`.
   - **Not yet answered:** how much of each dossier panel existing tables already cover (D2). Not
     measured here; do it with step 5, when the panels are defined.
   - **Caveat:** the file is from the 2026-10-06 run (82 claims, 82 with assertions), so it reflects the
     current claim model.
2. **Entity table in the model** *(done 2026-10-07, verified on the reference dump)* — built once from observation and claim subjects.
   - Shape: `EntityRecord(Id, Kind, JoinKey, Fidelity)`, immutable attributes only (§ 4.4). Display names
     are not stored: for type, module and thread the `JoinKey` is already the name, and method and object
     get names when their dossiers are built (M2c).
   - Built by `EntityTableBuilder` (`Reporting/Services`), attached as `AnalysisReportDocument.Entities` in
     `ReportOutputWriter` next to the side-car reference. Single-dump reports only; trend reports carry none.
   - Ids are dense and assigned after collection, sorted by kind then ordinal `JoinKey` (D4).
   - Same entity at different fidelity: the lowest is kept, so the table never overstates identity trust.
   - `report.json` has it automatically; the embedded HTML keeps it (it removes only lineage fields and the
     side-car reference), pinned by a test so a later edit does not drop it.
   - Gate met: suite green (1673 passed, 66 skipped); order-independence and identical-output tests in
     `EntityTableTests`.
   - **Real dump (3.35 GB reference, 2m22s, 2026-10-07):** 14,588 entities, matching step 1 exactly
     (14,233 type, 255 module, 38 thread, 55 object, 7 method); ids dense from 0; fidelity 13,472 Exact,
     86 High, 1,030 Low. Table is 2.2 MB compact JSON. `report.json` 87.5 → 90.6 MB; HTML 2.7 → 2.92 MB
     (the embedded payload, a gzip+base64 envelope `{report, perDumpDocs}`, carries `entities`). Old files
     kept in `m2-step1/before-step2/` beside the dump.
   - **Not checked:** that two real runs give byte-identical tables (unit tests only; one real run done).
   - **Noticed:** an object's `JoinKey` embeds the full dump path (`<dump path>:0x...`), so the 55 object rows
     repeat the path. Fine for now; revisit when object dossiers (M2c) are designed.
3. **Entity-typed columns and entity cells (D1)** *(done 2026-10-07, 4 batches; batch 4 uncommitted)* — the entity table's wire reference.
   - **What it buys (corrected 2026-10-07):** not payload size. The string pool already collapses repeated
     type names to small numbers, so moving them to the entity table is roughly size-neutral (step 1's
     8.4 MB figure was measured before pooling). The gain is that type cells become linkable to a dossier.
   - A column is marked an entity column in its builder through `CompactHeader.Type` (e.g. `entity:type`),
     decided per column, not by header name: `Types`, `Top Types`, `Live Types` and `Held Lock Types` may be
     lists, and `Wait Type` / `Task Type` may not be .NET types. No new column-typing mechanism.
   - One shared pass at write time swaps names for entity ids, for `report.json` and the HTML only, so
     Markdown and text still see names. A name missing from the entity table stays as text.
   - `ReportStringPool` must skip entity columns, with a test: both use plain numbers in a cell, so an id read
     as a pool index would corrupt it.
   - The HTML client gets an id-to-name lookup, beside its two existing cell translators (render and
     download-JSON); the download must resolve entity ids back to names.
   - **Batches (decided 2026-10-07):** a few tables at a time, each batch verified on the reference dump
     before the next. Batch 1 starts with the largest type columns from step 1 (finalizer queue, Memory,
     GC Generation, Leak Candidate, Object Shape, Allocation Pattern). Goldens re-bless per batch.
   - **Decided 2026-10-07 (cell format, option B):** the entity table merges compiler-generated names
     (`DisplayClass132_0` and `DisplayClass18_1` both become `...+<>c__DisplayClass`, fidelity Low), but tables
     show the exact name; ~6% of the cells in the biggest type columns are such names (measured:
     `m2-step1/column-match-results.txt`). An entity cell therefore carries the entity id, plus the exact name
     only when it differs from the entity's own name. Nothing is lost and every type cell stays clickable.
     Rejected: leaving merged names as text (closures, often the leak suspects, would not be clickable) and
     making the table keep exact names (contradicts the canonicalization rule).
   - **Decided 2026-10-07:** entity ids go into both `report.json` tables and the HTML, so the two files agree.
     Searching `report.json` for a type name inside a table finds an id; the entity table in the same file resolves it.
   - Gate per batch: real-dump payload size before and after; fingerprints unchanged.
   - **Mechanics (built in batch 1):**
     - A builder marks an entity column with `CHType("Type")` (header type `entity:type`).
     - `EntityCellPass` rewrites those cells at render time, in `report.json` and the HTML only; Markdown and text
       read the in-memory tables, which are never rewritten.
     - `EntityCell` is written as a bare id, or `[id, "exact name"]` when the shown name differs from the entity's.
     - A name with no entity stays a literal string. `ReportStringPool` skips entity columns.
     - Client: one `resolveEntityCell` in `report.renderers.shared.js`, used by the table renderer and the
       download-JSON path. Cells are resolved back to names before display, so the page looks the same; clickable
       links wait for the dossier (step 5).
     - `EntityCell` must be listed in `ReportJsonContext` (source-generated serializer); a unit test caught this.
   - **Batch 1 (done 2026-10-07):** finalizer queue entries (Type Name), Memory "Top types", GC Generation
     "Per-type generation profiles", Leak Candidate "Top leak candidates". Suite green (1681 passed, 66 skipped);
     no goldens needed re-blessing.
     - **Real dump (2026-10-07, 1m14s):** 110,228 cells in the four columns: 2,619 are `[id, name]` pairs, 718 are
       literals with no entity, the rest bare ids. Resolving them back gives **0 mismatches against the previous run**
       in both `report.json` and the HTML payload. Script and results: `m2-step1/verify-batch1.py`,
       `verify-batch1-results.txt`.
     - **Size:** `report.json` 90.56 → 85.21 MB (-5.4 MB, `report.json` has no string pool, so those type names were full literals in every row and are now ids);
       HTML 2.946 → 3.021 MB (+75 KB, the `[id, name]` pairs for 2,619 merged names).
     - **Entity table identical across the two real runs** (14,588 entries): the determinism check that was pending.
     - **Not browser-tested:** the JavaScript changes were checked by reading and by decoding the payload in Python,
       not by opening the report in a browser. Do this before the next batch.
     - **Pre-existing run-to-run variance, unrelated to this work:** String Analysis "Types by string field ownership"
       and Event Leak "Root Hint" differ between two runs on the same dump.
   - **Batch 2 (built 2026-10-07, uncommitted):** Object Shape "Gen2-retained types", Allocation Pattern (4 tables: long-lived,
     medium-lived, transient, high Gen1 survival), Event Leak "Publisher Type" (3 tables), GC Root "Target Type" (3 headers;
     one table has rows in this dump). Suite green (1681 passed, 66 skipped).
     - **Real dump (1m20s):** 13 entity columns, 140,862 cells (4,494 `[id, name]`, 801 literals); 9 columns and 30,634 cells
       added by this batch. **0 mismatches** against the pre-batch-1 names, in `report.json` and the HTML payload.
       Script and results: `m2-step1/verify-batches.py`, `verify-batch2-results.txt` (baseline is `before-batch1/`).
     - **Size:** `report.json` 85.21 → 82.68 MB; HTML 3.021 → 2.906 MB (smaller this time: the string pool no longer holds
       these names, and few of them need an `[id, name]` pair).
     - **Not changed on purpose:** Event Leak "Subscriber Type" (names are truncated to 100 characters, so some will not
       match an entity), Object Shape "Value Type" (a Yes/No column, not a type).
   - **Batch 2 committed** as `e2684999`; batch 1 as `26e91be1`.
   - **Batch 3 (built 2026-10-07, uncommitted):** String (3 tables incl. "Dominant Type"), Array (4), Boxing (3, incl. "Value Type"
     in the boxed-types table, which does hold type names), Finalizable (3 remaining), JIT, LOH & POH (2), WCF (2), GC Handle (8),
     Weak Reference (3), GC Generation (2), Type System (3). 13 builder files. Suite green (1681 passed, 66 skipped).
     - **Real dump (1m14s):** 43 entity columns, 158,534 cells (4,583 `[id, name]`, 1,939 literals); this batch added 30 columns and
       17,672 cells. **0 mismatches** against the original names in `report.json` and the HTML payload
       (`m2-step1/verify-batch3-results.txt`).
     - **Size:** `report.json` 82.68 → 82.41 MB; HTML 2.906 → 2.889 MB.
     - **Why 1,939 literals:** types with no entity (short names such as `SqlCommand`) and truncated names. They stay text.
   - **Batch 3 committed** as `c7722c50`.
   - **Batch 4 (built 2026-10-07, uncommitted):** Dominator (7), Crash (8 incl. "Inner Exception Type"), Async Task (8), Async State
     Machine "Declaring Type" (2), Module, Event Leak "Subscriber Type", Timer Leak, HTTP, Reference Chain, Hang, DB Connection (3),
     Thread exception types (2), Collection (4), SQL Command and SQL Transaction "objects by type". Suite green apart from the known
     flaky `ArrayAnalyzerRetypingCharacterizationTests` (passes alone, also on the previous commit).
     - **Real dump (1m42s):** 79 entity columns, 158,873 cells (4,586 `[id, name]`, 1,954 literals). **0 mismatches** against the original
       names in `report.json` and the HTML (`m2-step1/verify-batch4-results.txt`). `report.json` 82.41 → 82.40 MB; HTML 2.889 → 2.888 MB.
       Few cells added (339): these tables are small.
     - **Left as text on purpose** (not a .NET type name, or matches no entity): Object Shape and TypeTable "Value Type" (Yes/No),
       SQL "Outstanding commands" and "Active transactions" Type (short names such as `SqlCommand`), Collection "Collections by GC
       generation", Crash "Inner Type" (placeholder dashes), Async Task "Result Type" and "Exception Type" (no matches), Async State
       Machine "Type Name" (no matches), "Top Types" lists, handler-method names.
     - **Pre-rendered HTML (large reports) skips the pass,** same as the string pool.
4. **Thread retention observations** *(done 2026-10-07, verified on the reference dump; uncommitted)* — per D3.
   - **Extra-work check (answered):** the dominator-tree build runs only when an active analyzer carries the
     `IRequiresDominatorTreeIndex` marker (`DiskBackedObjectIndexWriter`). `ThreadAnalyzer` does not add it: it declares
     `[OptionalCapability(heap.dominators)]` and reads `context.HeapDominators`, which `AnalysisContextBuilder` fills from the
     cache when the tree exists. So this triggers nothing new; Thread Analysis took 16 ms on the real run.
   - **What it does:** `ThreadAnalyzer` emits one `thread.retained-bytes` observation (`retained.bytes`, `ThreadRef` subject) after each
     alive thread's `thread.state`, only when `TryGetThreadRetainedSize` returns a value. No tree, or a thread with no stack roots,
     emits nothing: absent means "not measured", never 0.
   - **Registered** in `schema/DumpDetective.Schema/observation-type-registry.json` (registry version 1.93.0 → 1.94.0) and in the
     conformance test's known-types list; both are required by `SdkRegistryConformanceTests`.
   - **Tests:** 3 new in `ThreadAnalyzerRetypingCharacterizationTests` (value emitted only for threads the tree knows; a thread
     with no stack roots emits none; no dominator tree emits none). Suite green: 1684 passed, 66 skipped.
   - **Real dump (1m21s):** observations 97,408 → 97,417 (+9, all `thread.retained-bytes`). 38 alive threads, 9 with stack roots,
     and all 9 got a value; none with roots was missing one. Largest: thread 24088 retains 22.7 MB, 2392 retains 10.1 MB, 11768 retains
     2.2 MB. Entities unchanged (14,588). Results: `m2-step1/step4-results.txt`.
   - **Not shown anywhere yet:** nothing in the report reads these observations until the thread dossier (step 7).
5. **Type dossier** *(5a `e6661e52` and 5b-1 `e3c47be7` committed; 5b-2 panel built; user opened it in a browser 2026-10-07 and found it good for now, refinement later)* — panels filled from the facts that name a type. Catalog of the 45
   fact kinds that name a type on the reference dump: `m2-step1/type-observation-catalog.txt`.
   - **Same number from several analyzers (decided: folded).** Instance count comes from ~15 fact kinds and shallow bytes from ~17.
     The page does not merge them or pick a winner (that would be judgment, which the entity layer does not make). It shows one row per
     measure and value, naming every analyzer that reported it: `Instances 54,068 (Leak Candidate Analysis, Finalizable Object
     Analysis)`. When sources differ, the values split into separate rows, so a disagreement stays visible.
   - **Panels** (assignment is a table keyed by fact kind):
     - Size and population: leak-candidate, finalizable-population, array-composition, boxing, known-pattern, timer, HTTP, SQL, WCF.
     - Generations and heaps: generation-type-profile, top-allocator, top-accumulator, LOH composition, large/pinned/sparse arrays, long strings.
     - Retention: type.retention, retention-hotspot, root-reachability, reference-chain sample, `heap.root`, static and event publisher facts.
     - Lifecycle: finalizer-queue entries, weak-handle targets, pinned and ref-counted handles, orphaned tasks.
     - Shape and cost: boxing, struct-padding waste, oversized value, reference-heavy, value-heavy, collection waste.
     - Exceptions and async: exception-type, active-exception, crash-thread-candidate, async state machine, captured closure.
     - Fixed parts: Claims (claims that cite its facts), Relations (slot, "not built yet", see M2b), Not measured.
   - **Heavy types show totals, not rows.** `DataColumn` has 54,005 finalizer-queue facts: the page shows a count and summed measures per
     fact kind; raw rows stay in the side-car (D2).
   - **5a — per-entity summary (built and verified 2026-10-07):**
     - `EntitySummaryBuilder` + `EntityFactPanels` (panel table and the summable-measure allow-list) + `EntitySummary` models in `Reporting`;
       attached as `AnalysisReportDocument.EntitySummary` in `ReportOutputWriter`, next to `Entities`; single-dump only.
     - **Judgment calls resolved with the defaults:** event-publisher facts under Retention; pinned handles and orphaned tasks under
       Lifecycle; leak candidates under Size and population; `infra.sql-connection-pool` left in Other; `runtime.crash-thread-candidate` stays
       on the type page until step 6. Module facts (JIT frames, footprints, unknown identity, HTTP handler module) are in Other until M2c.
     - **Summing is an allow-list**, not a deny-list: only reviewed counts and shallow-style byte totals are added across facts of one kind;
       anything else (retained bytes, averages, ratios, flags, new measures) gets min and max only, so an unreviewed measure can never be
       summed wrongly.
     - **Output shape:** `entitySummary: { factKinds, measures, entities }`; each entity is one raw line
       `[id,[[factKind,count,[[measure,sum|null,min,max]]]]]`. The raw line matters: `report.json` is indented, and indenting nested arrays
       cost +19 MB on the first real run (82.4 → 101.6 MB); as one line per entity the cost is +2.2 MB.
     - **Tests:** 10 new in `EntitySummaryTests` (grouping, single fact, allow-list sum, retained bytes never summed, unreviewed measure never
       summed, unmapped kind to Other, two-subject observation, no-subject observation, order independence, wire shape, `report.json` and
       embedded HTML). Suite green: 1694 passed, 66 skipped.
     - **Real dump (1m05s):** 14,588 entities, 17,205 fact groups, 54 fact kinds, 83 measures. Recomputing every group and every one of the
       144,227 stats independently from the side-car gives **0 mismatches**; the HTML payload carries the same summary as `report.json`.
       Sizes: `report.json` 82.40 → 84.59 MB, HTML 2.888 → 3.103 MB (+0.21 MB, as estimated). Script and results:
       `m2-step1/verify-summary.py`, `verify-summary-results.txt`. Panels: Size 10, Generations 8, Retention 8, Lifecycle 7, Shape 7,
       Exceptions 5, State 3, Other 6.
     - **Not yet read by anything:** the type page (5b) is the first consumer.
   - **5b — the page (design decided 2026-10-07):**
     - **Where:** a slide-over panel from the right, so the reader keeps their place in the report; Esc or a close button dismisses it.
       (Rejected: reusing the narrow right rail, which already holds the action queue; and a full new section, which loses the reader's place.)
     - **Click and URL:** a type name in an entity column becomes a link-styled cell; clicking opens the panel and sets the URL to
       `#entity/type/<name>`. Opening that URL directly opens the panel. A name with no entity shows a "not found" panel, never a silent miss.
       Closing restores the previous URL. The existing hash handlers treat any hash as an element id; the entity form must not trip them.
     - **Cells keep their id:** today `resolveEntityCell` turns an id into a name before the cell is drawn, so the id is lost. The cell
       must keep it (`data-entity-id`) and one delegated click handler on the main area reads it.
     - **Claims panel:** claim cards already have an anchor built in the browser from the claim id (`finding-<slug>`), and the HTML keeps
       every claim id (82 of 82 unique), so each claim in the panel links to its card.
     - **5b-1 — claim links in the summary (built 2026-10-07):** `entitySummary.claimIds` plus a third element on an entity's row
       (`[id,[groups],[claimIndex,...]]`, only when a claim cites it). Linked by the observations a claim cites (`derivedFrom`) and by a
       claim's assertion subject. Only claims that reach an entity are listed, ids sorted for determinism.
       - **Found on the real dump:** the full finding list has 83 claims but the report shows 82. `gc root analysis::subgraph-walk-capped`
         is in the list but in neither the cross-domain nor any domain insights, and it cites only process-wide numbers, so it linked to
         nothing and was occupying an id; now dropped. Why it is not shown anywhere was not investigated.
       - **Tests:** 5 new in `EntitySummaryTests` (cited observation links every entity it names; direct subject links without an
         observation; order independence; unlinked claim not listed; wire shape). Suite green apart from the known flaky Array test.
       - **Real dump (1m19s):** 40 of 82 claims link to entities (matches step 1's count); 2,218 entities have claims. Recomputed
         independently from the side-car and claim lineage: **0 mismatched entities**, id list identical; every id exists in the HTML. For
         `DataColumn`: LOH array pressure, high Gen2 finalizable count, finalizer queue retaining ~578.6 MB, DataTable lifecycle pattern,
         leak candidate cluster. Size: `report.json` 84.59 → 84.61 MB, HTML 3.103 → 3.108 MB. Results: `m2-step1/verify-claims-results.txt`.
     - **5b-2 — the panel (built 2026-10-07; not yet opened in a browser):**
       - **Files:** new `Templates/report.renderers.entities.js` (registered in the csproj, the inline bundle and the module barrel), CSS appended to
         `report.utilities.css`, small changes in `report.renderers.shared.js` (`describeEntityCell`), `report.renderers.sections.js` (entity cells carry
         their id into the table) and `report.main.js` (`setEntityData`, `setupEntityPanel`). No server-side change.
       - **Behaviour:** an entity cell becomes a link `#entity/<kind>/<encoded name>`; the browser's own hash handling opens the slide-over (so
         middle-click, copy-link and the back button work). Esc or the close button closes it and restores the address the reader came from.
         Moving to any other address (for example a claim link) closes it. Names are written with `textContent` only, never as markup.
       - **Contents:** identity and fidelity (a Low entity explains that numbered compiler-generated names are merged), counts of claims and facts,
         Claims (most severe first, each linking to its finding card when the card exists), then one block per fact kind under its panel, then the
         Relations slot ("Not built yet"), then a line naming the panels with no facts, then a note that raw facts are in the side-car.
       - **Numbers:** one fact shows its value; several facts show `total X (min, max)` when the tool allowed summing, and a range when it did not
         (retained bytes, ratios). A measure that never varies shows one value. Bytes are in human units with the exact count as a tooltip.
       - **Not done, deliberately:** folding the generation measures across fact kinds (decided earlier). The panel lists each fact kind separately; the
         fold is a space-saver, not a correctness feature, and can be added if the pages feel repetitive.
       - **Existing behaviour checked by reading:** the TOC/permalink click handler only intercepts `.toc a, .permalink`, and the `hashchange` handler
         ignores an address that matches no element id, so neither interferes. Table cell clamping skips any cell that contains a link.
       - **Pre-rendered HTML (large reports)** has no links, same as the string pool and entity cells.
       - **Verification (`m2-step1/panel-test.mjs`, results in `panel-test-results.txt`):** the real inline bundle from the generated HTML passes
         `node --check`; the real panel module is run in Node against the real payload from that HTML, with a minimal stand-in for the page. 32 of 32
         checks pass: link and address round trip (including a closure with an exact name), `DataColumn` page content (5 claims, 54,005 facts, shallow
         bytes as a total, retained bytes as a range), a card-less claim shown as text, a thread page, Esc and address restore, unknown and broken
         addresses, markup in a name, leaving the address closes the panel. A C# test pins that the bundle contains the panel.
       - **Not verified:** how it looks (spacing, colours, width on small screens), real focus and scroll behaviour, the panel over the report's other
         fixed elements, and opening a claim card inside a collapsed section. These need a person to open the report.
   - **An unmapped fact kind goes under an "Other" panel with its name,** so a new analyzer cannot make facts disappear.
   - **Summary design (decided 2026-10-07):** built at write time from the observations, next to `EntityTableBuilder`,
     attached to the document beside `Entities`, in both `report.json` and the HTML. Estimated 0.16-0.21 MB in the HTML (measured above).
     - Shape: a document-level list of fact kinds (name, analyzer, panel) and a list of measures (name, unit), then per entity a list of
       `[factKind, count, [[measure, value, min, max]]]` using indexes into those lists, so names are stored once.
     - Combining rule per measure, decided at build time: one fact gives its value; several facts of unit Count or Bytes are summed,
       except `retained.bytes`, ratios, percents and ordinals, which give count, min and max only.
     - Panel assignment is a table keyed by fact kind (in `Reporting`), with unmapped kinds under "Other". Draft for review, covering all
       54 fact kinds that name an entity on the reference dump (45 name a type): `m2-step1/panel-mapping-draft.md`. Pending review: six judgment calls
       listed at its end.
     - The generation measures (`gen0/gen1/gen2.count`, `gen2.bytes`) are the only ones folded across fact kinds.
     - Determinism: fact kinds and measures indexed in sorted order, so identical input gives identical output (same rule as D4).
   - **Per-entity summary in the HTML (option A) measured 2026-10-07, from saved observations, no dump loaded**
     (`m2-step1/measure-entity-summary.py`, `measure-entity-summary-results.txt`): all 14,588 entities have facts, across 54 fact kinds
     and 83 measure names. Count plus one value per measure costs **1.28 MB raw, 0.16 MB in the HTML** (gzip+base64); with
     sum/min/max per measure 2.06 MB raw, 0.21 MB in the HTML. 12,601 entities have exactly one fact. Cheap enough: option A is the plan.
   - **Two problems the measurement found (to resolve before building):**
     - **Same measure name, different meaning.** 3,600 (entity, measure) pairs are reported by more than one analyzer and 1,198 of
       them differ. Example: `System.Byte[]` `retained.bytes` is 24-5,144 from GC Root, 23,064,297 from Dominator, 88 from Reference
       Chain, because each means retained bytes of a different thing (a root, a tree node, a sample). So folding by measure name alone
       would put unlike numbers side by side as if comparable. Fold key must include the fact kind or an explicit meaning.
     - **Sums can be wrong.** Summing `retained.bytes` over many facts double-counts overlapping retained sets (54,005 finalizer-queue
       entries of `DataColumn`). Sums are valid for counts and shallow bytes only; retained bytes need min/max or a count, not a sum.
   - **Fold-safety measured 2026-10-07** (`m2-step1/measure-catalog.py`, `fold-safety-results.txt`; supersedes the "folded" wording above):
     - Even `instance.count` and `shallow.bytes` are not safe to fold by name. Of types reported by more than one fact kind, 556 of 1,524
       differ on `instance.count` and 629 of 1,430 on `shallow.bytes`: kinds cover different subsets (event-publisher facts count only
       leaking instances, array-composition only arrays, finalizer-queue entries are one row per object).
     - Only the generation family agrees: `gen2.count` differs for 1 of 557 types; `gen2.bytes` for 0 of 3; `gen0/gen1.count` come from
       generation-profile-style facts. `retained.bytes` differs for 23 of 50 types.
     - **Rule decided 2026-10-07:** group by fact kind, one labelled row per kind with its own measures; no folding across kinds
       except an allow-list of the generation measures (`gen0.count`, `gen1.count`, `gen2.count`, `gen2.bytes`). Within a kind with many
       facts: sum counts and shallow bytes, show count + min + max for `retained.bytes`, never a sum.
6. **Thread facts name their thread** *(done 2026-10-07, verified on the reference dump; uncommitted)* — measured in step 1: the
   active-exception and crash-thread-candidate observations pointed at the exception *type* only, so the thread page would have missed exceptions.
   - **Change:** `CrashAnalyzer` adds a `ThreadRef` (OS thread id) as a **second** subject on `runtime.active-exception` and
     `runtime.crash-thread-candidate`. The exception type stays `Subjects[0]`, which is where `CrashSynthesisRule`, `ActiveCrashInsightRule` and
     `FatalExceptionOnHeapInsightRule` read it; checked by reading every consumer. The trend differ only looks at facts with no subjects.
   - **Registry:** both types now `type+thread` in `observation-type-registry.json` (1.94.0 → 1.95.0), following the existing `type+method` precedent.
   - **Thread ids:** the crash analyzer's thread query already carries the OS id, so no id mapping was needed; the managed id is not set, same as
     the thread analyzer's own facts.
   - **Not done:** full stack traces. The report still carries only a top frame per thread; storing full traces remains a separate decision.
   - **Tests:** 1 new in `CrashAnalyzerRetypingCharacterizationTests` (both facts get the thread second, type first); suite green apart from the
     known flaky Array test. Panel harness: 3 new checks, 35 of 35 pass.
   - **Real dump (1m22s):** observation count unchanged (97,417), entities unchanged (14,588), all 82 claim ids and cross-domain titles/severities
     identical to before. The `IndexOutOfRangeException` facts now name thread 24088, which is also the thread with the largest retained bytes
     (22.7 MB); its entity row holds exception, crash-candidate, retained-bytes, stack-cluster and state facts. Results: `m2-step1/step6-results.txt`.
   - **Panel:** the thread page now expects an "Exceptions and async" panel and lists it as absent for threads without one.
7. **Thread dossier** *(done 2026-10-07, verified on the reference dump; uncommitted)* — the panel already rendered threads (step 5b); this step makes thread
   cells reach it.
   - **Which columns:** the 7 "OS Thread" columns (Thread Analysis 4 tables, Crash Analysis 2, Hang 1) use `CHThread` (header type `entity:thread`).
     The managed "Thread ID" / "Thread" / "Managed Thread" columns stay plain numbers: the entity table keys threads on the OS id, and none of the managed ids match.
     On the reference dump 6 of the 7 columns have rows (the Hang table is empty).
   - **Cells:** the pass turns an OS thread number into the thread's entity id. Section builders hand over thread ids as `double`, not `uint`: a first
     version only handled integer types, passed its unit tests, and converted nothing on the real dump; caught by the real-dump check, now covered by a test.
   - **Wire ambiguity avoided:** in an entity column a bare number means "entity id", so a thread number with no entity becomes its digits as text, never a
     leftover number (the same hazard the string pool avoids).
   - **Looks and behaviour unchanged:** link text keeps the thousands separators the number cell had ("24,088"), the cell keeps `data-value` so numeric
     sorting still works, and download-JSON resolves a thread id back to a number. The panel title is now "OS thread 24088".
   - **Tests:** 3 new in `EntityCellTests` (double and integer cells, unknown/placeholder cells, a thread number never matched against a type entity);
     suite green (1704 passed, 66 skipped). Panel harness 40 of 40 (5 new: thread link, separator, sort value, text fallback, title).
   - **Real dump (1m09s):** 6 thread columns, 65 cells: 41 entity cells each pointing at the right thread (name equals the original OS id), 24 text cells
     (the "—" placeholders in the exception-instances table); **0 mismatches**; entity table and summary identical to the previous run.
     Sizes: `report.json` 84.61 MB (unchanged), HTML 3.127 MB. Results: `m2-step1/verify-step7-results.txt`.
   - **Not done:** thread links from outside tables (for example from claims or the header), full stack traces in the page.
8. **Anchors** *(done 2026-10-08, uncommitted)* — [§ 9.5](ReportSystemVision.md#95-citation-and-anchoring).
   - **Already true, from steps 5-7:** `#entity/type/<name>` and `#entity/thread/<os id>` open the page. The address is the entity's own name or OS id,
     not a numbered id, so it is stable across re-renders of one dump; an address from another dump finds no entry and the panel says so, rather than
     opening a different object.
   - **"A broken anchor is a build failure", decided 2026-10-08: report loudly, still write the report** (a reader is better served by a report with one
     dead link than by none). `EntityReferenceValidator` runs in `ReportOutputWriter` just before the sinks publish, and prints a warning with the count
     and the first 10 problems.
   - **What it checks:** entity ids equal their row; every summary row names an entity in the table; every fact-kind, measure and claim index is in
     range; every listed claim id is a finding; every entity cell in a table points inside the entity table at the kind its column holds; no bare number
     is left in an entity column.
   - **Tests:** 7 in `EntityReferenceValidatorTests`, one per kind of break plus the clean cases; suite green (1711 passed, 66 skipped).
   - **Real dump (1m25s):** no warnings. That shows no false alarms; detection is shown by the unit tests, not by the real run.
   - **Deferred, with their milestones:** `#claim/...`, `#obs/...`, `#q/...` and the `/retention` sub-panel suffix (M5 and later). Claim cards already
     have their own `#finding-...` anchors.

## Open questions

- **Cost of the entity index at 25 GB scale.** [§ 21 Q7](ReportSystemVision.md#21-risks-assumptions-and-open-questions):
  a dossier for a type with millions of instances is per-entity aggregate and should be fine, but any
  graph-shaped panel needs its boundary stated in `coverage.boundary` rather than a silent cap.
- **`Subjects = []` sites.** The 43 process-wide scalars have no entity; the plan assumes they stay
  that way. A `process` entity kind would be a later addition and is not needed for type/thread.
- **Thread identity.** Answered by step 1, corrected 2026-10-07: `ThreadRef` has an optional `ManagedThreadId`, but the
  thread observations in the reference dump set only `osThreadId`, which matches the retention provider.
  The thread dossier shows OS id unless emitters start filling the managed id.

## Deferred work (do not drop)

- **More entity kinds and dossiers (wanted).** Tracked as milestone **M2c** in
  [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path). Kinds in this dump today
  (step 1): module 255, object 55, method 7. They already have an `EntityRef` subtype, so they get entity-table
  rows in M2 but no pages.
  - **Order:** module first (255 entities with real facts: JIT frames, disk and heap footprint), then
    object and method, then the kinds below.
  - **GC root, handle, memory segment, AppDomain** have no `EntityRef` subtype and no analyzer names them
    as an observation subject yet. Each needs the subtype in the SDK, analyzers to emit it, and the
    `EntityKind` / JSON discriminator added. Check with the SDK review findings before adding kinds.
  - **M2 is not complete** until this list exists in the plan and in § 19; it is not a build task for M2.
- **Relations (D5).** Deferred out of M2 but wanted. Tracked as milestone **M2b** in
  [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path) and under *Open questions* in
  [m1-claims-plan.md](m1-claims-plan.md#open-questions).
  - **What is missing:** claims have no `relations` field, so the dossier cannot say "held by this static
    field", "subscribed to by this event", or "contradicts claim X".
  - **What M2 leaves behind:** a Relations slot in the dossier layout, shown as "not built yet" (never
    hidden, never faked from root-chain text).
  - **When to pick it up:** after M2 ships, or earlier if M3 or M5 needs it. M2 is not complete until the
    slot exists and says so.
