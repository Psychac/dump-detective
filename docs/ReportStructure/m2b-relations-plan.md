# M2b — Relations: Implementation Plan

Follows [m2-entities-plan.md](m2-entities-plan.md) (D5, deferred) and [m2c-entity-kinds-plan.md](m2c-entity-kinds-plan.md) (done 2026-10-09).
Tracked as milestone **M2b** in [ReportSystemVision.md § 19](ReportSystemVision.md#19-minimum-viable-path). It fills the dossier's Relations
slot, which today reads "Not built yet" (`entityRelations` in `report.renderers.entities.js`).

**Status:** in progress. D1-D6 decided 2026-10-09; steps 1-2 done 2026-10-09, steps 3-4 pending.

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
| D2 | A relation read from a table row is labelled "appears with" and names the table, never "holds" or "retains" | **Decided 2026-10-09.** A row says two entities appear together, not why. Keeps D5 of the M2 plan: relations are never faked from table text. A relation from a two-subject fact is labelled with its fact kind instead, and wins when both sources give the same pair. |
| D3 | No cap on the relation list; large groups render collapsed | **Decided 2026-10-09.** The project forbids top-N sampling. A big module has thousands of types, so the panel groups by kind and uses the shared collapsible tree, with the full count in the group heading. |
| D4 | Store each pair once, with a list of sources, and let the client derive both directions | **Decided 2026-10-09.** About 14,000 module-type pairs would otherwise be written twice. Payload cost is measured in step 1 before the shape is fixed. |
| D5 | Compute row relations from the output of `EntityCellPass`, in the same place as the validator | **Decided 2026-10-09.** That pass is where cells resolve to entity ids, so the relation list cannot disagree with the links the reader sees. `report.json` and the HTML must carry the identical list. |
| D6 | The panel is HTML-only, like the rest of the dossier | **Decided 2026-10-09.** Markdown and text keep showing tables. |

## Steps

1. **Untyped relations from table rows** *(done 2026-10-09, verified on reference dump)*
   - **Measured first** (`m2-step1/m2b1-measure-results.txt`): 14,039 row co-occurrences are 13,177 distinct pairs from 11 tables; 436 pairs are seen in more than one table. Stored as `[first, second, [source...]]` they cost 223 KB, 0.3% of the 80.7 MB report, so D4 held and no client-side derivation was needed.
   - **Server:** `EntityRelationBuilder` reads the rows `EntityCellPass` has rewritten; `Apply` attaches the result as `Relations` (a source list plus pairs, lower id first), so the HTML, `report.json` and the validator share one list (D5). The in-memory document is unchanged.
   - **Validator:** `ValidateRelations` checks ids are inside the entity table, the pair is lower id first and not self-related, every pair names at least one source, every source index is in range.
   - **Panel:** the "Not built yet" note is replaced by an "Appears with" note (D2) and one group per kind, in the order Type, Module, AppDomain, Thread, Method, Object. A group of 25 or more starts collapsed and fills in when opened (D3), so no neighbour is dropped. The source shows once on the group line when the whole group shares it, otherwise on each item.
   - **Tests:** 5 `EntityRelationBuilderTests` (order, dedup with sources, text/self/single-column ignored, attached by `Apply`, validator); harness 26 of 26 (6 new: both directions, sorting, collapse and lazy fill, mixed sources, wording, empty).
   - **Real dump:** 13,177 pairs and 11 sources, identical to the measurement; all pairs canonical; 13,233 of 14,590 entities have a neighbour (largest: mscorlib.dll 5,307, System.Data.Entity.dll 3,031, System.Core.dll 753); report.json +224 KB, HTML +50 KB; entity table, summary and claims identical; 0 warnings. Spot checks: thread 24088 lists `System.IndexOutOfRangeException`, method `PortableTimer::OnTick` lists its declaring type, the first object lists `SqlConnection`. Results: `m2-step1/verify-m2b1-results.txt`.
   - **Not run-identical, not caused by this step:** the String Analysis "Types by string field ownership" values differ between runs (162.3, 166.1, 163.5 and 158.6 MB for the first row across four runs, three of them before any relations code). Not investigated here.
   - **Open question answered:** 13,167 of the 13,233 entities have exactly one neighbour (a type and its module), so single-entry groups are the common case. They are kept: it is the only link between a type page and its module page.
2. **Relations from two-subject facts** *(done 2026-10-09, verified on reference dump)*
   - **How:** `EntityRelationBuilder.FromFacts` pairs every two subjects of an observation and names the pair `fact: <fact kind>`; `ReportOutputWriter` stores them on the document beside the entity table, and `EntityCellPass.Apply` merges them with the row pairs (`Merge`). A pair seen both ways keeps only its fact source (D2). Merging twice gives the same result.
   - **Determinism:** sources are sorted by name when written, so the list does not depend on the order observations arrived in. This also re-orders the row sources of step 1 (names only).
   - **Panel:** a fact source reads "in fact `type.async-state-machine`"; the note now says "seen in the same table row, or as two subjects of one fact".
   - **Tests:** 8 more `EntityRelationBuilderTests` (pair named by fact kind, fewer than two known subjects ignored, three subjects give three pairs, arrival order, fact wins, merge idempotent, merge of nothing, `Apply` keeps writer pairs); harness 28 of 28 (2 new).
   - **Real dump:** 62 pairs added (55 SQL transaction type to connection object, 7 method to state-machine type), 1 pair re-sourced to its facts (thread 24088 and `IndexOutOfRangeException`), 0 lost; 13,239 pairs, 13 sources; entity table, summary and claims identical; 0 warnings. Results: `m2-step1/verify-m2b2-results.txt`.
   - **Found, not fixed:** the state-machine type in `type.async-state-machine` facts is the entity `OnTick`, not the full type. `EntityCanonicalizer`'s async-state-machine rule drops the declaring type, so `PortableTimer+<OnTick>d__8` and `PeriodicBatchingSink+<OnTick>d__16` are one Type entity and its page lists both methods. Pre-existing identity behaviour that relations make visible; changing it moves entity ids and trend identity, so it is a separate decision.
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

- Whether a thread's or module's AppDomain should also become a column in the existing tables once step 3 exists.

## Not in this plan

- Claim-to-claim relations (D1).
- New entity kinds (GC root, handle, memory segment): revisit only if a typed relation in step 4 needs one ([M2c step 7b](m2c-entity-kinds-plan.md)).
- Relations in the Markdown and text outputs (D6).
- Trend or cross-dump relations.
