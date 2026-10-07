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
3. **Entity-typed columns and entity cells (D1)** *(pending)* — the entity table's wire reference.
   - A column is marked an entity column through `CompactHeader.Type` (e.g. `entity:type`); no new
     column-typing mechanism. Entity cells carry the entity's integer id.
   - Producers resolve the name to an entity; low-fidelity types (closures, async state machines) keep
     their fidelity flag.
   - `ReportStringPool` must skip entity columns, with a test: an id read as a pool index would corrupt the cell.
   - Markdown, text and the HTML client resolve ids back to names. Goldens for the affected sections
     re-bless once, in one reviewed step.
   - Gate: real-dump payload size before and after; fingerprints unchanged.
4. **Thread retention observations** *(pending)* — first check whether requesting the thread-retention value from `ThreadAnalyzer` makes the tool do extra work on every run (declare it optional, and confirm Stage B is not newly triggered). — per D3. Needs the analyzer change; check that it
   stays a raw measure and does not add new judgment.
5. **Type dossier** *(pending)* — Appendix B panels that today's observations can fill: identity,
   population, generations, retention, lifecycle, claims, coverage. Panels without data are listed as
   absent, not hidden.
6. **Thread facts name their thread** *(pending, before the thread dossier)* — measured in step 1: the
   active-exception and crash-thread-candidate observations point at the exception *type* only, so the
   thread page would miss exceptions. Add the thread as a second subject on those observations
   (`ThreadAnalyzer`, crash analyzer). Check first what thread id each emits (tables show managed and OS
   id; `ThreadRef` has OS id only). Full stack traces: step 1 found only a top frame per thread in the
   report; whether to store full traces is a separate decision, not made here.
7. **Thread dossier** *(pending)* — same layout; first report surface for per-thread retention.
8. **Anchors** *(pending)* — `#entity/type/<name>`, `#entity/thread/<id>`. A broken anchor fails the
   build ([§ 9.5](ReportSystemVision.md#95-citation-and-anchoring)).

Each step is verified on the reference dump, one run at a time.

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
