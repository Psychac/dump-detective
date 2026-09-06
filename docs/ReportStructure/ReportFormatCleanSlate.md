# Report Format — Clean-Slate Redesign

## Status

Design proposal. Nothing here is implemented. One of three baseline documents for the reporting
layer — see [README.md](README.md). The incremental single-dump and trend format specs this
supersedes were deleted 2026-09-06 and are in git history; no document describes the shipped
report, so read the code for current behavior.

## Purpose

Define what the HTML report should be if designed from scratch today — single-dump and multi-dump,
one file, `file://`, no server.

## Thesis

**The report has no editorial function.**

Analyzers compute everything they can and hand it to a format that has no opinion about what
matters, and no obligation to explain any of it. Every problem below is a face of that one:

- A table titled "**Top** finalizer queue entries" contains 68,576 rows, of which 20 render.
  Nothing decided what belonged in a report versus what belonged in an archive.
- 37 sections carry 15.7 KB of prose between them. Nothing is required to say what a table means.
- The payload inflates to 30.9 MB in the browser, because "ship all of it" is what a format with
  no editor does.

The byte count is a symptom, not the disease. Fixing the container without fixing the editorial
layer produces a report that opens instantly and still doesn't tell you what's wrong.

Measured evidence for all of the above is in [Appendix A](#appendix-a--measured-baseline); it is
evidence, not the argument.

---

## 1. An editorial contract

The load-bearing change. Everything else is easier once this exists.

### 1.1 Mandatory lead

Every widget carries a lead, checked at build time:

```json
{
  "lead": {
    "what": "Every object currently on the finalizer queue, by retained bytes.",
    "abnormal": "68,576 entries — the queue is not draining.",
    "action": "Check the finalizer thread stack in D1 for a blocked finalizer.",
    "cites": ["B6:4#col=Total Bytes", "D1:0#stack=Finalizer"]
  }
}
```

`what` is always present. `abnormal` and `action` are present when a threshold fired and
explicitly `null` otherwise — so "nothing notable here" is a stated conclusion a reader can trust,
not an absence they have to interpret.

### 1.2 Honest titles

Titles are generated from the data, never hand-written:

```json
{ "titleSpec": { "noun": "finalizer-queue entries", "sortBy": "Total Bytes", "rowCount": 68576 } }
```

renders as *"All 68,576 finalizer-queue entries, sorted by retained bytes — showing 50."* A title
cannot drift from its rows, and it cannot imply curation that didn't happen.

### 1.3 Progressive disclosure is an editorial decision

Default view = what a reader needs to reach a conclusion. Everything else is one click away and
still in the file. This is not sampling: nothing is dropped, nothing is capped, the full set is
present and exportable. It is the difference between *publishing* and *dumping*, and it is decided
per widget by the producer, not by a global row cap.

This decision also defines the payload's lazy-segment boundary (§7) — the container falls out of
the editorial choice, not the other way round.

### 1.4 Citations

[EvidenceRef](../../src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs) exists today but
is passive. Every number in prose becomes a chip: click it and the exact rows or column that
produced it scroll into view and highlight. The inverse holds too — a row shows which findings
cite it.

A number in prose with no resolvable citation is a **test failure** (§9), not a style nit. This is
what makes a generated report defensible to a skeptical reader, and it is the only mechanism that
keeps narrative honest as analyzers change underneath it.

---

## 2. One renderer, one closed widget vocabulary

Today the format is defined by code: 12 typed slots plus an 18-case `SectionBlock` union in
[AnalyzerDetailSection.cs](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs), a
matching bespoke renderer in JS for each, and a **second** server-side renderer
([ReportHtmlShared.cs](../../src/DumpDetective.Reporting/Formatters/ReportHtmlShared.cs)) that has
to stay visually in sync with the first. Every new analyzer adds display code in two places.

Replace with declarative view specs over a fixed set. Adding an analyzer requires zero new JS.

| Widget | Replaces | Notes |
|---|---|---|
| `table` | `CompactTable`, `TableBlock` | typed columns, `titleSpec`, lead |
| `tree` | `TreeWidget` | keep as-is — already the right shape |
| `chain` | `RootOwnedSubgraphGroup`, `TypeSampleTrace` | ordered hops as entity refs + field names |
| `stack` | `NamedStackTrace`, `StackFrameBlock` | frames + framework flag + meta strip |
| `cluster` | `StackCluster` | signature + member ids, built on `tree` |
| `distribution` | `ChartBlock`, `SparklineBlock` | histogram / treemap / sparkline over a column |
| `timeline` | trend sparklines | snapshot-indexed series |
| `kv-strip` | `KeyMetrics`, `MetricBlock` | typed metric values |
| `callout` | `LeadFinding`, `ConfidenceBandBlock`, `InterpretationBlock`, `NextStepsBlock` | severity, confidence, caveats, links |
| `entity-card` | `LeakCandidateCard`, `EventLeakGroupCard`, `EventLeakInstanceCard` | entity ref + typed fields + nested widgets |
| `code` | `PathBlock`, artifact instructions | copyable, monospace |

`entity-card` absorbing the three card types is the load-bearing consolidation — three render
paths and 6.5 MB of one report collapse into one.

### 2.1 One renderer, not one per report type

Single-dump and trend must stay a **mode of one renderer**, not two renderers. They already are:
[report.main.js:157](../../src/DumpDetective.Reporting/Templates/report.main.js#L157) and
[report.renderers.header.js:9](../../src/DumpDetective.Reporting/Templates/report.renderers.header.js#L9)
branch on `doc.$kind === 'trend'`.

Splitting them would repeat the pre-render mistake on a new axis — two renderers obliged to stay
visually identical, drifting on the first one-sided fix — and it would make §5.3's diff mode
impossible, since that renders a *single-dump* report as a delta using the same widgets.

The split belongs at the **composition** layer: trend composes the same widgets from the metric
store, plus three trend-only ones (heatmap, swimlane, timeline).

### 2.2 Template source layout

18 JS files and 6 CSS files whose names describe neither ownership nor boundaries. Measured:

| Symptom | Evidence |
|---|---|
| Filename lies about contents | `report.renderers.header.js` is 60 KB for **three** functions — `buildHeader`, `buildHealthScorecard`, `buildExecutiveSummary`. Two of the report's most important surfaces live in a file called "header". |
| No boundary between layers | `report.renderers.sections.js` (65 KB) holds format helpers, a treemap builder, section assembly, the correlation timeline, **and** `renderTrendDumpGroups` — trend rendering inside "sections". |
| Duplicate concerns | `report.renderers.panels.js` defines `buildGlobalSearchBar` and `buildFilterBar` while `report.ui.search.js` and `report.ui.filters.js` exist as separate files. |
| Leftover bucket | `report.ui.js` is 24 KB for two functions (`buildSidebar`, `setupInteractivity`) alongside eight `report.ui.*.js` siblings. |
| CSS split by page region, not component | `.analyzer-section` is styled across `report.base.css`, `report.detail.css`, **and** `report.header.css`; `.detail-block` across `report.detail.css` and `report.utilities.css`. |

**This has already produced a live bug.** `formatBytes` is defined twice with different semantics —
[report.dom.js](../../src/DumpDetective.Reporting/Templates/report.dom.js) (`Math.round` to 2 dp,
returns `"0 B"` for non-numeric) and
[report.renderers.sections.js](../../src/DumpDetective.Reporting/Templates/report.renderers.sections.js)
(fixed decimals per unit, signed, returns `""` for non-finite). `BuildInlinedBundle` flattens every
file into one closure scope and appends `sections.js` after `dom.js`, so **the second definition
silently wins for every caller**, including callers written against the first. `1024` renders as
`"1 KB"` or `"1.0 KB"` depending on which file the author was reading.

Target layout: one file per widget (§2), one shared `format` module, one `state` module, one
`bootstrap`; CSS as tokens + one file per widget, so a component's styles are never split across
files. A real bundler (§8) makes the collision above a build error instead of a coin flip.

Full analysis and design for this layer — design system, component library, code architecture,
budgets, migration — is in the sibling doc
[ReportTemplateCleanSlate.md](ReportTemplateCleanSlate.md).

---

## 3. Entities and pivot views

The report is organized by analyzer. Investigation happens by **type**, **thread**, or **root**.
That mismatch is the biggest information-architecture failure in the current format, and it costs
readers more time than any byte does.

Promote the string pool into a typed entity table:

```json
{
  "types":   [ { "id": 0, "name": "System.Data.DataColumn", "mt": "0x7ff8", "short": "DataColumn" } ],
  "threads": [ { "id": 0, "osId": 1234, "managedId": 42 } ],
  "modules": [],
  "addrs":   []
}
```

Every widget references entities by id, which buys two things:

1. **Type dossier.** Click any type; get every claim any analyzer made about it in one panel —
   leak score, dominator rank, gen2 fraction, exact retained bytes, string byte ownership, event
   subscriptions, finalizer-queue presence. Today that means reading nine sections and correlating
   by hand.
2. **Thread and root dossiers**, the same way. The `RootStackThreadAttribution` /
   `IThreadRetentionProvider` work already computes per-thread retention and is currently unwired
   to any report surface — the thread pivot is its natural consumer.

Three axes over one evidence store, no extra analysis cost. Deduplication (131,568 type names
currently repeated ~1M times) is a free side effect, not the motivation.

---

## 4. Interaction

- **Expression filter**, not section-name search. Honest, uncapped tables make real filtering
  mandatory: `type:*Channel size>10mb gen2>0.8 root:static cited-by:critical`
- **Console API** `dd.query(…)` over the evidence store. Zero server, full power-user escape
  hatch, no caps reintroduced.
- **URL-hash state** for view, filters, expansion — a teammate opening the same file lands on the
  same view. Partially present today via `history.replaceState`.
- **SOS handoff** per finding: copy the exact `!dumpheap -mt … / !gcroot … / !do …` sequence that
  reproduces the claim in WinDbg. This converts skeptics faster than any chart.

---

## 5. Multi-dump

### 5.1 Problem

[TrendReportDocument](../../src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs#L200)
embeds `PerDumpDocuments` (hand-compacted to 7 keys by `CompactPerDumpJson`) **and**
`TrendAnalyzerSections` — two parallel truths, scaling O(dumps × sections).

### 5.2 Replacement

Three long-format tables; every trend visual is a query over them:

```text
metrics   : (metricKey, snapshotIdx, value)
findings  : (fingerprint, snapshotIdx, severity, present)
snapshots : (idx, dumpPath, capturedAtUtc, processUptimeMs, provenance)
```

Per-snapshot evidence stays in per-snapshot segments, materialized only on drill-in.

### 5.3 What it unlocks

- **Severity heatmap** (domain × snapshot) as the hero visual — `SeverityHistory` already computes
  the data.
- **Finding lifecycle swimlanes** by fingerprint: appeared / persisted / resolved / **flapping**.
  Flapping is invisible in today's new/persistent/resolved counters, and it's the signal that
  separates a real regression from noise.
- **Leak-rate regression**: least-squares bytes-per-hour per type with R² —
  *"`Foo` grows 12 MB/h, linear, R² = 0.98 → exhausts the 2 GB budget in ~6 h."* This is the reason
  to capture multiple dumps at all. Needs `capturedAtUtc`, which the filename timestamps already
  carry.
- **Diff mode**: pick any two snapshots and re-render the *single-dump* report as a delta. Only
  possible because single and trend share one renderer (§2).

---

## 6. Presentation

### 6.1 Visual system

154 KB of CSS across 6 files with zero `prefers-color-scheme` means the palette is hardcoded in
hundreds of places. Rebuild as a token layer plus one grid, target ~12 KB:

- `light-dark()` + `color-scheme` — dark mode with no duplicated rule blocks, no toggle logic.
- Severity by **label + shape + color**, never color alone.
- `font-variant-numeric: tabular-nums` on every numeric cell — metric columns must align.
- **Density toggle** (comfortable / compact); these are data-dense documents.
- One categorical chart palette, contrast-validated in both schemes.

### 6.2 Print

A real `@media print` pass: sections auto-expand, nav and rails drop, page breaks per domain, links
footnoted with targets, provenance on page 1. These get printed into postmortems.

### 6.3 The file as its own archive

**Extract** button writes `report.json` from the embedded evidence, and a visible provenance footer
carries dump hash, analyzer version, scoring model version, elapsed time, and coverage gaps. One
file is both viewer and archive — no separate JSON artifact to lose or drift.

---

## 7. Payload

Implementation detail of §1.3, not a design goal in itself. The editorial contract decides what is
eager; this section says how to carry it.

- **Segmented envelope.** A ~40 KB shell plus independently gzipped segments: `core` (provenance,
  scorecard, actions, findings, TOC, leads, default rows, column stats), `entities`,
  `tbl:<id>` per table, `sec:<id>` per non-tabular section. `DecompressionStream` is already used
  for the single blob in [report.main.js](../../src/DumpDetective.Reporting/Templates/report.main.js)
  — same call, per segment.
- **Column-major, dictionary-encoded tables.** Per column: a type tag, a dictionary, a packed
  value array; entity columns store ids. Makes group-by, histograms, treemaps, and fast sorting
  possible client-side — which is what §4's filter and §3's dossiers need.
- **Nulls are null; booleans are booleans.** `CompactHeader` already carries type/format metadata
  and the stated intent is that formatting is the client's job. Today the payload still ships
  `"Yes"`, `"No"`, `"N/A"`, and `—` as cell values because the C# side formats for the text
  renderer and HTML inherits the artifacts. One canonical evidence model, N presenters.
- **Retain:** gzip + base64 (proven under `file://`) and the small-report plain-JSON threshold at
  [HtmlReportRenderer.cs:31](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs#L31),
  which keeps small reports greppable.

Effect on the measured report: 30.9 MB inflated → ~4–6 MB total, ~1.5 MB eager.

---

## 8. Delete list

| Delete | Why |
|---|---|
| `PreRender` path + [ReportHtmlShared.cs](../../src/DumpDetective.Reporting/Formatters/ReportHtmlShared.cs) | a second renderer, for a problem §1.3 solves properly |
| `ReportStyleVersion` v1/v2 dual path | two visual systems, one product |
| `StripModuleKeywords` and its `Regex.Replace` of `Dom.` / `R.` / `UI.` prefixes | runtime regex munging of JS; use a build-time bundler |
| `catch { … report.js }` fallback in `BuildInlinedBundle` | silent degradation to a 3 KB toy renderer nobody notices |
| 11 of 12 typed section slots; the 18-case `SectionBlock` union | replaced by §2 |
| `Templates/report.css` (279 B) and duplicate `wwwroot/css/report.css` | orphans |
| `TrendReportDocument.PerDumpDocuments` + `CompactPerDumpJson` | replaced by §5.2 |

**Keep:** `CompactHeader`'s typed/format metadata, stable section ids + `SectionIdDomainMap`,
finding fingerprints, confidence bands with caveats, gzip+base64 transport, the plain-JSON
threshold, `TreeWidget`.

### 8.1 Verified deletion inventory

Scope confirmed against the tree — all pure deletion, no new behavior, no dependency on the rest
of this document. Roughly 900+ lines of C#, ~4 KB of JS, three config/doc surfaces.

| Target | Sites |
|---|---|
| `PreRender` | `ReportOptions:10`, `AnalysisCommandRequest:23`, `AnalyzerOptionsBuilder:48`, `CliConfigurationModels:64`, `ConfigurationResolver:293`, `TrendOrchestrationService:139`, `BuildReportStage:32`, `HtmlRenderSettings`, 3 `{{PRE_RENDERED_*}}` placeholders, `report.main.js:99-102` |
| `ReportHtmlShared.cs` + `ReportHtmlSharedAnchorTests.cs` | whole files — the only production caller is the pre-render branch at `HtmlReportRenderer.cs:52-56` |
| `ReportStyleVersion` | enum, `RootCommandBuilder:168-178`, `ConfigurationParseHelpers:25-36`, `ReportOptions:9`, `config.sample.json:10`, `report.main.js:79-82`, 19 `body.report-style-v2` rules in `report.base.css` made unconditional, 6 test call sites |
| Fallback + orphans | `catch { … report.js }`, `Templates/report.js` (3.2 KB), `report.renderers.js` (380 B), `Templates/report.css` (279 B), duplicate `wwwroot/css/report.css` |

Two facts worth recording before this is executed:

- **"v2" is smaller than its name.** It resolves to exactly two behaviors: a
  `body.report-style-v2` class gating 19 CSS rules (right rail, summary stagger, anchor flash), and
  an opt-out from the auto-pre-render heuristic. **No JavaScript branches on it** — there are zero
  `styleVersion` references outside `report.main.js:79-82`. It is a layout flag that was never
  promoted, not a visual system.
- **The default is the style you moved past.** `ReportOptions:9`, `HtmlRenderSettings.Default`,
  `ConfigurationResolver:292` and `config.sample.json:10` all default to `V1`, while the reports
  actually being generated and reviewed are v2.

### 8.2 What versioning to keep

Version the data contract; never version the look.

| | Keep? | Why |
|---|---|---|
| Style / presentation version | **No** | The renderer ships *inside* the file with its payload. A report from six months ago renders with the renderer it was born with, so an old payload is never fed to a new viewer. The problem style versions solve does not exist for self-contained artifacts. |
| `SchemaVersion` (currently `2.1`) | **Yes** | Real external surface: JSON export consumers, trend comparison across runs from different tool versions, golden baselines. Already covered by [schema-versioning.md](../schema-versioning.md). |
| `ScoringModelVersion` / `ActionScoringModelVersion` | **Yes** | Provenance, not presentation — answers "why did this score 72 when last month's scored 61". |

To A/B a layout in future, use a build-time flag on a branch, not a runtime enum shipped to users.

---

## 9. Budgets and tests

Build-time gates, failing CI. The editorial ones matter more than the size ones:

| Gate | Threshold |
|---|---|
| Widget without `lead.what` | 0 |
| Number in prose without a resolvable citation | 0 |
| Hand-written table title (no `titleSpec`) | 0 |
| Widget kind outside the closed set (§2) | 0 |
| Unresolved in-page anchor | 0 |
| String cell in a `number` / `bool` column | 0 |
| Cell equal to `N/A`, `—`, `Yes`, `No` | 0 |
| Duplicated type name across the payload | 0 |
| App shell (HTML + CSS + JS, excl. segments) | ≤ 120 KB |
| `core` segment, inflated | ≤ 2 MB |
| Total payload, inflated | ≤ 8 MB for a 175K-row report |
| Time to first meaningful paint | ≤ 150 ms on the Appendix A report |

[HtmlRendererCssTests](../../tests/DumpDetective.Tests/Integration/HtmlRendererCssTests.cs) is the
natural home for the static gates. The size and timing gates need a golden fixture built from a
real payload — a synthetic one will not reproduce 131,568 distinct type names.

---

## 10. Phased migration

Ordered so every phase changes what a reader sees. Encoding work is interleaved, not front-loaded.

| Phase | Scope | Reader-visible effect |
|---|---|---|
| **P0a** | Turn on what exists: populate `strings`, land `CompactRowJsonConverter` on the HTML path | opens faster; no format change |
| **P0b** | §8.1 deletion inventory — `PreRender`, `ReportHtmlShared`, style v1/v2, the bundle fallback, orphan files (blocked on open question 6) | nothing visible; removes the drift surface |
| **P1** | §1.1–1.3 — mandatory leads, generated titles, per-widget default views | every table says what it means and stops lying about "Top" |
| **P2** | §2 — closed widget set + §2.2 template layout, one file per widget, shared `format` module | one renderer; new analyzers need no JS; kills the `formatBytes` collision |
| **P3** | §3 — entity table + type dossier | investigate by type instead of by analyzer |
| **P4** | §1.4 — citations both directions | every claim traceable to its rows |
| **P5** | §7 — segmented envelope + column store, boundary defined by P1 | instant open on any dump size |
| **P6** | §6.1–6.2 — tokenized CSS, dark mode, density, print | usable at night and on paper |
| **P7** | §4 — expression filter, console API, URL state, SOS handoff | uncapped tables become navigable |
| **P8** | §5 — trend metric store, heatmap, swimlanes, leak-rate, diff mode | multi-dump answers "how fast" |
| **P9** | §3 thread/root dossiers, §6.3 archive + provenance | remaining pivots; file self-describes |

P0 is a pure win with no format break and no design commitment; it can land regardless of whether
the rest is adopted.

---

## 11. Open questions

1. **Are `strings` and `CompactRowJsonConverter` genuinely off on the shipped HTML path, or absent
   only from the Appendix A build?** ~11.8 MB and P0's entire scope hinge on whether this is
   "never wired up" or "a regression."
2. **Column store as base64 binary or plain JSON arrays?** Binary is ~2× smaller but gives up
   greppability. Proposal: JSON arrays below the existing 200 KB threshold, binary above — the
   same policy that already exists, on one more axis.
3. **Do the Markdown/text formatters consume the same evidence model?** They should — that is how
   the em-dashes got into the HTML data — but
   [MarkdownCanonicalReportFormatter](../../src/DumpDetective.Reporting/Formatters/MarkdownCanonicalReportFormatter.cs)
   and [TextCanonicalReportFormatter](../../src/DumpDetective.Reporting/Formatters/TextCanonicalReportFormatter.cs)
   would each need their own formatting layer. Not scoped here.
4. **Is a build-time JS bundler acceptable**, or must the bundle stay pure-MSBuild? §8 assumes the
   former.
5. **Who is the default reader?** The editorial contract in §1 needs a target: on-call engineer
   triaging in 10 minutes, or specialist doing a deep post-incident analysis. The two want
   different default views. Current answer is "both, badly."
6. **Does anything depend on pre-render for a no-JS context** — email, SharePoint preview, an
   external PDF pipeline? [HtmlReportRenderer.cs:50](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs#L50)
   silently forces pre-render for v1 reports over 2 MB of JSON or 1000+ findings, which means the
   *largest* reports currently take the least-tested path. That is an argument for deleting it, but
   somebody added the heuristic for a reason. Blocks §8.1.

---

## Appendix A — measured baseline

Source: `D:\DUmps\Crash_IIS_BALTSTPRD\Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.html`,
from a 3.35 GB crash dump. Measured 2026-09-04. Supporting evidence for §§1–7; not the argument.

### A.1 File composition

| Layer | Size |
|---|---|
| CSS — one inlined `<style>` from 6 embedded files | 154 KB |
| JS — one inlined IIFE from 18 embedded files | 303 KB |
| Payload — base64 of gzip | 3.38 MB |
| Payload — gzip bytes | 2.54 MB |
| **Payload — inflated JSON, `JSON.parse`d on every open** | **30.9 MB** |
| Total file | 3.7 MB |

37 sections, 9 domains, 36 analyzers, 60 domain findings, 20 ranked actions. `domains` is 99.9% of
the payload. Narrative `blocks` across all 37 sections total **15.7 KB**.

### A.2 Largest sections

| Bytes | Domain | Id | Analyzer | Dominant slot |
|---|---|---|---|---|
| 7,707,273 | GC | B6 | Finalizable Object Analysis | `compactTables` 7,705,331 |
| 4,102,958 | TypeSystem | C2 | Object Shape Analysis | `compactTables` 4,100,899 |
| 3,916,411 | Threads | D4 | Event Leak Analysis | `eventLeakInstanceCards` 2,590,191 |
| 3,739,049 | Memory | A5 | GC Root Analysis | `rootOwnedSubgraphGroups` 3,498,005 |
| 2,672,248 | Leaks | A1 | Leak Candidate Analysis | `compactTables` 2,655,206 |
| 2,401,384 | Memory | A2 | Memory Analysis | `compactTables` 2,397,602 |
| 2,213,994 | Memory | A7 | String Analysis | `compactTables` 2,211,554 |
| 2,118,028 | GC | B2 | Allocation Pattern Analysis | `compactTables` 2,114,818 |
| 2,098,748 | GC | B1 | GC Generation Analysis | `compactTables` 2,096,153 |

### A.3 Tables

142 tables, **175,158 rows, 1,821,559 cells**, all shipped eagerly.

| Table | Rows | Cols | `rowLimit` | Bytes |
|---|---|---|---|---|
| B6 "Top finalizer queue entries by estimated retained size" | **68,576** | 10 | 20 | 7,678,521 |
| A1 "Top leak candidates by suspicion score" | 14,003 | 12 | 20 | 2,653,555 |
| A2 "Top types" | 13,884 | 8 | 20 | 2,396,696 |
| A7 "Top duplicate strings" | 15,294 | 10 | 20 | 2,140,792 |
| B1 "Per-type generation profiles" | 14,003 | 10 | 20 | 2,092,920 |
| C2 "Gen2-retained types" | 11,027 | 16 | 20 | 2,049,433 |

Non-table bulk: A5 ships 395 root-owned subgraphs carrying **71,944** hop strings; D4 ships 2,975
instance cards + 1,027 group cards.

### A.4 Waste attributable to the container

| Waste | Measured | Cause | Recoverable |
|---|---|---|---|
| String cells not pooled | 1,022,984 cells, 131,568 unique | `strings` key absent — [ReportStringPool](../../src/DumpDetective.Reporting/Formatters/ReportStringPool.cs) not applied on this path | **~9.9 MB** |
| Row object wrapper | 175,158 × `{"values":[…]}` | `CompactRowJsonConverter` not in effect in this build | **~1.9 MB** |
| Presentation in data | 259,887 `"No"`, 136,425 `"N/A"`, 66,946 `"Yes"`, 52,975 `—` | C# formats for the text renderer | ~516K cells → bool/null |
| Shipped-vs-displayed | 68,576 rows shipped, 20 displayed | no editorial boundary | 7.7 MB → ~40 KB eager |
| Repeated type names in hops | 71,944 hop strings | no entity table | 3.5 MB → ~0.4 MB |

The 52,975 U+2014 cells are a deliberate "no value" placeholder (`A1/Root Chain`,
`A2/Est. Retained`, `A2/LOH Bytes`, `A1/Root Kind`, `A5/Field`), **not** an encoding fault — the
payload contains zero `EF BF BD` byte sequences.

---

## Appendix B — reproduction

```python
import re, base64, gzip, json, collections

html = open(PATH, encoding='utf-8').read()
b64  = re.search(r'id="report-json"[^>]*>(.*?)</script>', html, re.S).group(1)
raw  = base64.b64decode(b64)
data = gzip.decompress(raw)
print(f'b64={len(b64):,} gz={len(raw):,} inflated={len(data):,}')

rep = json.loads(data)['report']
for k, v in sorted(rep.items(), key=lambda kv: -len(json.dumps(kv[1]))):
    print(f'{k:28} {len(json.dumps(v)):>12,}')

cells = collections.Counter()
for dom in rep['domains']:
    for sec in dom['sections']:
        for t in sec.get('compactTables') or []:
            for row in t['rows']:
                vals = row['values'] if isinstance(row, dict) else row
                for v in vals:
                    if isinstance(v, str):
                        cells[v] += 1

occ, uniq = sum(cells.values()), len(cells)
raw_b    = sum((len(k) + 3) * v for k, v in cells.items())
pooled_b = sum(len(k) + 3 for k in cells) + occ * 4
print(f'strings occ={occ:,} uniq={uniq:,} raw={raw_b:,} pooled={pooled_b:,}')
```

Set `PYTHONIOENCODING=utf-8` on Windows — several cells contain U+2014 and the cp1252 console will
raise on print.
