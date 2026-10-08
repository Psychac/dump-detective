# M2c — More entity kinds: Implementation Plan

Follows [m2-entities-plan.md](m2-entities-plan.md) (shipped 2026-10-08). Tracked as milestone **M2c** in
[ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path). The panel, summary and cell
mechanics already exist for types and threads; this milestone extends them to the other kinds.

**Status:** draft 2026-10-08, nothing built. D1-D4 decided 2026-10-08.

## Starting point (measured on the 3.35 GB reference dump, 2026-10-08)

| Kind | Entities | Fact kinds that name it | Where tables show it |
|---|---|---|---|
| Module | 255 | disk footprint (255), heap footprint (60), JIT frames (22), HTTP handler module (1), unknown identity (1) | "Module" columns in 7 tables, "Assembly" / "Full Path" in 4 |
| Object | 55 | SQL transactions only (55); all 55 have claims | "Address" columns in ~20 tables; only 55 addresses are entities |
| Method | 7 | async state machines only | no usable column |
| GC root, handle, memory segment, AppDomain | none | no `EntityRef` subtype, no analyzer names them | — |

- **Module names are unique on this dump:** 255 module facts, 255 distinct names.
- **Table path columns are truncated for display** ("..."), so shared-name questions cannot be answered from the report; the full path
  exists only inside `ModuleAnalyzer` while it runs. Results: `m2-step1/module-identity-results.txt`.
- **`ModuleRef`'s join key is the file name by design** (cross-source matching with a future trace; `ModuleAddress` is a non-identity handle).
- **One module entity has an empty name** (an unnamed or dynamic module). A link cannot point at an empty name.

## Decisions

| # | Decision | Status |
|---|---|---|
| D1 | Module identity: file name or full path | **Decided 2026-10-08: file name stays the identity; the full path becomes a detail on the page, never part of the key.** A path in the key would differ between machines and defeat cross-source matching, and would touch the SDK, the canonicalizer and every analyzer that emits modules. When two modules share a file name the page says so and lists the paths. Needs `ModuleAnalyzer` to emit the path (not yet looked at). |
| D2 | Which kinds get pages | **Decided 2026-10-08: every kind gets a page,** including objects and methods (reversing the first proposal to skip them). Consequence: each needs its own table columns marked, panels and a readable title; see steps 5-6. Their facts are thin on this dump (objects: SQL transactions only; methods: async state machines only), so those pages will mostly show identity, claims and a few facts. |
| D3 | The empty module name | **Decided 2026-10-08: no link, no page;** the cell stays text. |
| D4 | The four new kinds (GC root, handle, memory segment, AppDomain) | **Decided 2026-10-08: separate pieces after the existing kinds,** each needing an SDK subtype, analyzers that emit it, an `EntityKind` and JSON tag, and registry entries. The order is chosen when the existing kinds are done. |

## Steps (modules first)

1. **Module path as a detail** *(done 2026-10-08, verified on the reference dump; uncommitted)* — decided route A: the path is a detail of the entity, not a fact.
   - **How:** `ModuleAnalyzer` attaches the full path as `EvidenceRef` (locator `module-path`, name in `EntityDetailLocators` in the SDK) on each
     `runtime.module-disk-footprint` fact. `EntityTableBuilder` turns that into `EntityRecord.Details`: sorted, distinct, only for module entities, omitted
     from the JSON when empty. The identity (file name) is untouched.
   - **Panel:** a module with details shows "Loaded from:" and its paths; with two or more it says "N modules share this file name and are shown as one" in
     a warning colour and lists every path. The analyzer already detects shared names (`ModuleConflictGroup`); none fired on this dump.
   - **Tests:** 3 new in `EntityTableTests` (sorted/distinct paths, order independence, ignored on non-modules and for blank paths, written only when present);
     suite green (1714 passed, 66 skipped). Panel harness 43 of 43 (3 new, including the two-paths warning).
   - **Real dump (1m36s):** 254 of 255 module entities carry exactly one path (the empty-named module has none, matching D3); no non-module entity has
     details; entity table identical to before apart from `details`; summary, claims and observation count (97,417) identical; +43 KB of paths in
     `report.json`, HTML 3.127 → 3.135 MB. No validator warnings. Results: `m2-step1/m2c1-results.txt`.
   - **Not yet:** nothing links to module pages until step 2; the page already opens by address (module facts land in the "Other" panel until step 3).
2. **Module columns** *(done 2026-10-08, verified on the reference dump; uncommitted)*
   - **Measured first** (`m2-step1/module-columns-results.txt`): every "Module" column and the module table's "Name" column hold exact file names, except the
     JIT table, which shows full paths. The "Assembly" and "Full Path" columns hold paths truncated for display ("..."), so they match nothing and are left
     as text. Truncation also stops names longer than 55 or 45 characters from matching (1 name here).
   - **Change:** `EntityColumn.Module` and `CHModule`; 10 columns marked across the Module, JIT, HTTP, Memory, Array and TypeSystem builders. The pass matches a cell
     to a module by file name; a cell showing a path matches on the file name at its end and keeps the path as its exact text (`[id, "<path>"]`). Link text is
     unchanged, so tables look the same.
   - **Tests:** 4 new in `EntityCellTests` (direct match, a path with either slash, truncated / unknown / placeholder / folder-only cells stay text, a module is never
     matched to a type or thread with the same text). Panel harness 45 of 45 (2 new).
   - **Real dump (1m48s):** 10 module columns, 16,099 cells: 15,370 entity ids, 22 id-plus-path (the JIT table), 707 text (the unnamed module, truncated or
     missing names, placeholders). **0 mismatches** against the previous run's text; entity table and summary identical; `report.json` 84.44 MB (smaller, since module
     names became ids), HTML 3.135 MB; no validator warnings. Results: `m2-step1/verify-m2c2-results.txt`.
   - **Test-suite environment problem (resolved):** with C: full (0 bytes free) three temp-writing tests failed (`HeapIndexScanDispatcherTests`,
     `CacheContainerWriterChecksumProgressTests`, `RetainedSizeCandidateSelectorTests`), identically with this change stashed. After freeing space they pass (31 of 31).
3. **Module panels** *(done 2026-10-08, verified on reference dump)*
   - **Panels:** Footprint (disk, heap, type density), Activity (JIT module frames), Identity (HTTP handler, unknown identity, shared name, cross-domain load, assembly-reference mismatch) in `EntityFactPanels`.
   - **Client:** `ENTITY_MODULE_PANELS` added to the panel order; a module page expects Footprint (every module has a disk footprint fact).
   - **Tests:** `PanelFor_PlacesModuleFactsInTheirOwnPanels` (10 cases); entity suites 62 of 62.
   - **Panel harness:** the old one could not be found, so `tests/EntityPanelHarness/entity-panels.test.mjs` was written (fake DOM, `node --test <file>`): 5 of 5 pass (module panel order, expected Footprint note, type and thread pages unchanged).
   - **Real dump (101s):** 339 module facts moved out of "Other" (Footprint 315, Activity 22, Identity 2), 0 left; all 255 modules have a Footprint fact; entity table identical to the previous run; no validator warnings. Results: `m2-step1/verify-m2c3-results.txt`.
4. **Module anchors** *(done 2026-10-08, verified on reference dump)* — `#entity/module/<name>` already worked (the address is generic over kind); this step guards it.
   - **Validator:** `EntityReferenceValidator` now warns when two entities share an address, and when a table cell links an entity with an empty name (D3: that cell stays text).
   - **Bug found by the new check:** step 2's `ToModuleCell` matched an empty cell to the unnamed module's entity, so 6 cells linked to no address. Fixed with a guard; regression test `Apply_ModuleColumn_EmptyCellStaysTextEvenWhenAnUnnamedModuleEntityExists`. Step 2's verification missed it because it compared shown text, where `""` equals `""`.
   - **Harness:** 7 new cases (12 of 12): awkward names (dots, spaces and brackets, `#`, `%`, non-ASCII, `/` and `\`) round-trip through the link and the address; an unknown module says "no entry"; an unmatched cell stays text.
   - **Real dump:** 0 warnings after the fix; entity table and summary unchanged; no duplicate addresses. Results: `m2-step1/verify-m2c4-results.txt`.
5. **Method pages** *(done 2026-10-08, verified on reference dump)* — full version, decided 2026-10-08.
   - **Measured first:** only the two Async State Machine tables name a method ("Originating Method", "Method Name"); JIT tables stay text (no `MethodRef` is emitted there). 7 method entities, one fact each, no claims.
   - **Column:** `EntityColumn.Method`. The cell shows the bare method name (`[id, "OnTick"]`), but the entity key is `<declaring type>::<name>`, so the pass reads the declaring type from the same row's entity:type column headed "Declaring Type" (a validator warning fires when it is missing).
   - **Source fix:** the two async tables no longer truncate Declaring Type and method names; truncation ("...") had made 2-3 of 7 rows unmatchable.
   - **Page:** title is the key (`Class::Method`); a method page expects the "Exceptions and async" panel.
   - **Tests:** 4 `EntityCellTests` (same-row match, same name on another type, unknown/cut-off/empty stay text, no declaring-type column), 1 validator test; harness 15 of 15 (3 new).
   - **Real dump:** 7 of 7 method cells linked in both tables, method text unchanged, entity table and summary identical, 0 warnings. Declaring Type links 5 of 7: the other 2 types have no type entity. Results: `m2-step1/verify-m2c5-results.txt`.
6. **Object pages** *(done 2026-10-08, verified on reference dump)*
   - **Measured first:** 55 object entities, all `infra.sql-transaction`, and the entity is the *connection* (the fact's second subject), so the matching column is "Connection Address", not the transaction's "Address". Of 28 address-like columns (70,167 cells) only two match: SQL "Connection Address" (55 of 55) and the finalizer table's "Address" (55 of 68,576). Only those two are marked.
   - **Column:** `EntityColumn.Object`. The key is `<dump path>:0x...` but tables show the address, so the pass matches on the address alone (case-insensitive) and the cell keeps the shown text (`[id, "0x..."]`). If two entities share an address (a report spanning several dumps) neither is linked.
   - **Page:** title is the address, not the dump path; the anchor is `#entity/object/<encoded full key>`, so an address from another dump finds no entry and the panel says so. No expected-panel list (objects only ever carry the facts that name them).
   - **Tests:** 3 `EntityCellTests` (address match and case, unknown/placeholder text, shared address), harness 17 of 17 (2 new); entity suites 133 passed.
   - **Real dump:** all 55 object entities reached by a link (55 cells in each table), text unchanged, entity table and summary identical, 0 warnings. Results: `m2-step1/verify-m2c6-results.txt`.
   - **Not done:** other address columns (~26) stay text until an analyzer names those objects as entities.
7. **Remaining kinds** *(measured 2026-10-08; per D4 one kind at a time; AppDomain done, others pending)*
   - **Measured on the reference dump** (columns and facts only; no analyzer names any of these as a subject today):

     | Kind | Where it appears | Facts that concern it | Order |
     |---|---|---|---|
     | AppDomain | 2 domains (name, id, address, module count, bytes) in Module "AppDomain inventory" and Thread "AppDomain thread distribution" | none as subject; module cross-domain-load facts name modules only | **1st** |
     | GC root | 1,404 rows ("Top GC roots by severity": root kind, root address, target) plus root-kind columns in 5 more tables | `heap.root` has the *target type* as its only subject | 2nd, decide with handle |
     | GC handle | "Handle Kind" in 4 GC Handle tables; the "Address" there is the pinned *target*, not the handle (it equals "Target Addr" in the root table) | `handle.pinned`, `weakhandle.target`, `handle.refcounted-target`: all subject = the target type | merge into GC root, or defer |
     | Memory segment | 8 segments in 3 Heap Topology / LOH tables | none | last, or drop |

   - **Why this order:** AppDomain is the smallest (2 entities) and already joins three tables, so it is the cheapest first test of a new kind end to end. Pinned and strong handles are also GC roots (the first rows of the root table are both), so a separate handle kind would duplicate roots. Segments have no facts, so a page would show only an identity.
   - **Cost per kind (D4):** SDK subtype and JSON tag, an analyzer that emits it as a subject, `EntityKind`, table columns, panels, validator and harness cases.
   - **7a. AppDomain** *(done 2026-10-08, verified on reference dump)*
     - **SDK:** `AppDomainRef` (identity = name; the domain id is a dump-local handle), `EntityKind.AppDomain` (appended last, so no existing entity id moves), `$kind: "appdomain"` in `observation.schema.json`; registry 1.96.0 and a changelog entry.
     - **Facts (new):** `runtime.appdomain-footprint` from `ModuleAnalyzer` (`module.count`, `shallow.bytes`; unnamed domains are skipped, D3) and `thread.appdomain-population` from `ThreadAnalyzer` (`thread.count`; threads with no domain are not counted under any). Panels: Footprint and Activity; `module.count` and `thread.count` added to the summable measures.
     - **Columns:** `EntityColumn.AppDomain` on Module "AppDomain inventory / Domain Name" (no longer truncated to 60 characters) and Thread "AppDomain thread distribution / AppDomain".
     - **Tests:** one analyzer test in each of `ModuleAnalyzer` and `ThreadAnalyzer` characterization suites, 2 `EntityCellTests`, 2 panel-mapping cases, registry conformance; harness 20 of 20 (3 new); 150 passed.
     - **Real dump:** 2 AppDomain entities (ids 14588-14589, after every existing entity; existing entities and all 14,588 summary rows identical once names are resolved), each with a Footprint and an Activity fact; both domain tables link 2 of 2, text unchanged; claims and findings identical (observation GUIDs differ per run); 0 warnings. Results: `m2-step1/verify-m2c7-results.txt`.
     - **Not done:** a module-to-domain relation (relations are deferred M2b work); modules are not linked from the domain page.

## Open questions

- Which of the 7 "Module" columns hold a clean file name, and which hold paths (to be read from the builders, not the report).
- Whether dynamic and anonymous modules (the unnamed one, and generated assemblies such as `App_Theme_DevEx.*.dll`) deserve a different note on the page.
