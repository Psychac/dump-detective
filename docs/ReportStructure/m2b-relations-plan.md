# M2b — Relations: Implementation Plan

Follows [m2-entities-plan.md](m2-entities-plan.md) (D5, deferred) and [m2c-entity-kinds-plan.md](m2c-entity-kinds-plan.md) (done 2026-10-09).
Tracked as milestone **M2b** in [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path). It fills the dossier's Relations
slot, which today reads "Not built yet" (`entityRelations` in `report.renderers.entities.js`).

**Status:** draft 2026-10-09, nothing built. D1 decided 2026-10-09; D2-D6 proposed, awaiting a call.

## Goal

An entity page lists the other entities it is related to (a module's types, a type's module, a thread's exception type, a method's declaring type),
with the source of each relation stated, so an investigation can move sideways without going back to the tables.

## Scope

| Piece | What it is | Needs analyzer changes | In this plan |
|---|---|---|---|
| **Untyped entity relations** | "appears with", from table rows and two-subject facts | no | steps 1-2 |
| **Linking facts** | module to AppDomain, thread to AppDomain, emitted as two-subject facts | yes, small | step 3 |
| **Typed entity relations** | retains, held by a static field, subscribes to | yes, per source | step 4 (investigate first) |
| **Claim-to-claim relations** | supports, contradicts, duplicates, explains | no new entity data | out of scope, own plan when needed (D1) |

M3 (coverage), M4 (payload) and M5 (query) do not depend on any of this. M5's v1 needs entity, measure, severity, `cited-by` and `contradicted`,
and all of those already exist.

## Starting point (measured on the 3.35 GB reference dump, 2026-10-09)

| Source | Measured | Notes |
|---|---|---|
| Tables with two or more entity columns | 11 | Memory "Top types", Array "Top array types", Type table (2), Collection "Wasteful collections", Thread "Threads with active exceptions", Async (2), Crash "Exception instances", Module "Top types in heaviest modules", Finalizer queue |
| Entities with at least one row-based neighbour | 13,233 of 14,590 | 13,112 types, 60 modules, 55 objects, 5 methods, 1 thread |
| Row co-occurrences by kind pair | Module-Type 13,970; Object-Type 55; Method-Type 10; Type-Type 2; Thread-Type 2 | counted per row, not yet de-duplicated into distinct pairs |
| Facts with two subjects | 64 of 97,421, in 4 fact kinds | `infra.sql-transaction` (object + type, 55), `type.async-state-machine` (method + type, 7), `runtime.active-exception` (thread + type, 1), `runtime.crash-thread-candidate` (thread + type, 1) |
| Existing two-subject path | `EntityTableBuilder` already collects every subject | no SDK change needed to read them |
| Relations that need new emission | module to AppDomain, thread to AppDomain, retains, subscribes-to, held-by-static | the AppDomain tables list modules and threads as text, not entity cells |

## Decisions

| # | Decision | Status |
|---|---|---|
| D1 | Entity relations and claim-to-claim relations are separate pieces | **Decided 2026-10-09.** They have different data sources and payoffs; claim relations need no new entity kinds. Only entity relations are planned here. |
| D2 | A relation read from a table row is labelled "appears with" and names the table, never "holds" or "retains" | **Proposed.** A row says two entities appear together, not why. Keeps D5 of the M2 plan: relations are never faked from table text. A relation from a two-subject fact is labelled with its fact kind instead, and wins when both sources give the same pair. |
| D3 | No cap on the relation list; large groups render collapsed | **Proposed.** The project forbids top-N sampling. A big module has thousands of types, so the panel groups by kind and uses the shared collapsible tree, with the full count in the group heading. |
| D4 | Store each pair once, with a list of sources, and let the client derive both directions | **Proposed.** About 14,000 module-type pairs would otherwise be written twice. Payload cost is measured in step 1 before the shape is fixed. |
| D5 | Compute row relations from the output of `EntityCellPass`, in the same place as the validator | **Proposed.** That pass is where cells resolve to entity ids, so the relation list cannot disagree with the links the reader sees. `report.json` and the HTML must carry the identical list. |
| D6 | The panel is HTML-only, like the rest of the dossier | **Proposed.** Markdown and text keep showing tables. |

## Steps

1. **Untyped relations from table rows** *(pending)*
   - **Measure first:** distinct pairs after de-duplication, bytes added to `report.json` and the HTML, and how many pairs repeat across tables. Fix the payload shape (D4) from that, not before.
   - **Server:** a relations pass beside `EntityCellPass`; a `relations` list on the document (pairs of entity ids plus a source index) and a source list naming each table.
   - **Validator:** every pair names two existing entities, pair order is canonical, every source index is in range, no entity is related to itself.
   - **Panel:** replace the "Not built yet" note with grouped lists (by kind, then by source), collapsed past a size threshold (D3); each name links to its page.
   - **Tests:** pass and validator unit tests; harness cases for grouping, collapse and an entity with no relations.
   - **Real dump:** one run on the reference dump; compare entity table and summary against the previous run (must be identical), and spot-check one thread, one module, one method and one object page.
2. **Relations from two-subject facts** *(pending)*
   - **What:** the 64 existing two-subject facts, labelled by fact kind (D2), taking precedence over a row-based pair for the same two entities.
   - **Where:** `EntitySummaryBuilder` already walks facts per entity, so the second subject is read there.
   - **Check:** each fact-based pair also appears row-based where a table shows both, and nothing is shown twice.
3. **Linking facts: module to AppDomain, thread to AppDomain** *(pending)*
   - **Why separate:** the AppDomain tables hold modules and threads as text, so no row-based relation exists. `ModuleAnalyzer` already walks modules per domain.
   - **How:** new fact kinds with two subjects (module + AppDomain, thread + AppDomain) rather than changing the shape of existing facts, because existing insight rules read subjects by position. Registry, schema changelog and conformance test updated as in M2c step 7a.
   - **Open:** how many facts this adds (about 255 modules on the reference dump) against the observation volume the project watches.
4. **Typed entity relations** *(pending, investigate each source before committing to it)*
   - **Candidates:** retains and held-by-static (reference-chain and static-root data), subscribes-to (event-leak publisher and subscriber), GC root to target (the M2c step 7b decision named relations as the reason to revisit).
   - **Blocker to check first:** static-root detection never fires in the current code and its fix is held unmerged (see the project memory note), so held-by-static has no data until that is settled.
   - **Rule:** one source per sub-step, each measured on the reference dump first; a relation type ships only with a source that emits it, never as a placeholder.
   - **Panel:** typed relations replace "appears with" for the pairs they cover and keep their own labels.

## Verification (every step)

- One run per step on the 3.35 GB reference dump with the CrashAnalysis launch profile, in the foreground. Never the 25 GB dump.
- Entity table, per-entity summary, claims and findings compared against the previous run (observation GUIDs differ per run and are normalised).
- Validator reports 0 warnings; harness and focused unit suites green.
- Results written beside the earlier ones in `m2-step1/` on the dump drive.

## Open questions

- Payload cost of storing relations (step 1 measures it); if large, whether module-type pairs should be derived client-side from the existing Type table's module column instead of stored.
- Whether "appears with" is useful for the 13,112 type pages whose only neighbour is their module, or whether the panel should hide a group with a single entry.
- Whether a thread's or module's AppDomain should also become a column in the existing tables once step 3 exists.

## Not in this plan

- Claim-to-claim relations (D1).
- New entity kinds (GC root, handle, memory segment): revisit only if a typed relation in step 4 needs one ([M2c step 7b](m2c-entity-kinds-plan.md)).
- Relations in the Markdown and text outputs (D6).
- Trend or cross-dump relations.
