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
2. **Module columns** *(pending)* — mark the "Module" columns `entity:module` (new `EntityColumn.Module`, `CHModule`, `KindOf`,
   pass, validator and client). Cells that show a full path or a truncated path cannot be matched to a file-name key reliably;
   decide per column whether to link the "Module" (file name) column only.
3. **Module panels** *(pending)* — Footprint (disk, heap), Activity (JIT frames), Identity (HTTP handler, unknown identity) in
   `EntityFactPanels`; thread-style expected-panel list in the client; an identity note that lists shared-name paths.
4. **Module anchors** *(pending)* — `#entity/module/<name>`; validator and Node harness extended.
5. **Method pages** *(pending)* — decide which table cell names a method (none does today; "Declaring Type" in the async tables shows
   numbers), mark it `entity:method`, add panels (async state machine facts) and a title like `Class::Method`. May need an analyzer to emit the name
   in a table.
6. **Object pages** *(pending)* — an object's key embeds the full dump path (`<dump path>:0x...`) while tables show the bare address, so the pass
   must build the key from the address; only 55 of the addresses shown in ~20 tables are entities, the rest stay text. Panels from the SQL
   transaction facts; title shows the address, not the dump path.
7. **Remaining kinds** *(pending, per D4)* — GC root, handle, memory segment, AppDomain, one at a time.

Each step is verified on the reference dump, one run at a time, in the same way as M2.

## Open questions

- Which of the 7 "Module" columns hold a clean file name, and which hold paths (to be read from the builders, not the report).
- Whether dynamic and anonymous modules (the unnamed one, and generated assemblies such as `App_Theme_DevEx.*.dll`) deserve a different note on the page.
