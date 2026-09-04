# HTML report payload size reduction — design

## Motivation

After the top-N / capped-sample removal work, a single-dump HTML report for a 3.3 GB dump is
**29.9 MB on disk**, of which **29.4 MB is the embedded JSON payload**
(`{{REPORT_JSON}}` in `report.html`). That is the intended data volume — the project
deliberately emits exact, uncapped data (see the "no top-N sampling" rule) — but the payload
spends most of those bytes on *encoding overhead*, not on information.

The measured redundancy: **225,596 type-name cells consume 10.47 MB but hold only 14,963
distinct strings.** `"System.Data.DataColumn"` appears 54,019 times (1.29 MB of the payload);
the literal `"No"` appears 259,887 times (1.24 MB).

This document catalogues every reduction opportunity found, with measured (not estimated)
numbers, and sequences them. **No proposal here drops a row, a column, or a digit of
precision beyond stated float rounding** — reintroducing caps is explicitly off the table.

Reference artifact for all numbers below:
`D:\DUmps\Crash_IIS_BALTSTPRD\uncapped-report.js` — the extracted payload from
`Date__03_23_2026__Time_06_21_21PM__Second_Chance_Exception_E0434352.dmp` (3.3 GB, 36
analyzers, 75 s analysis).

## Measurement method

Numbers were produced by parsing the real payload and re-serializing after each transform, so
each delta is an actual byte count rather than a projection. Compression figures use Node's
`zlib` at level 9; browser behaviour was verified by building a real HTML file and loading it
headless under `file://`.

Note the two baselines that appear below: **29.43 MB** is the payload envelope on disk
(`{"report":…,"perDumpDocs":[]}`, UTF-8 bytes); **28.17 MB** is `JSON.stringify` of the parsed
document, which is what the transform deltas are measured against. The gap is envelope
wrapper plus non-ASCII characters that cost more than one byte on disk.

## Where the bytes are

| Path | MB | Notes |
|---|---:|---|
| `report.domains[].sections[].compactTables[].rows` | 21.44 | 142 tables, 175,158 rows, 1,821,559 cells |
| `…sections[].rootOwnedSubgraphGroups[].subgraphs[].hops` | 3.09 | 71,944 hop strings (all type names) |
| `…sections[].eventLeakInstanceCards[].subscriberDetails` | 1.39 | 5,835 entries |
| `…sections[].eventLeakGroupCards` | 0.66 | 1,027 cards |
| everything else | ~2.2 | executive summary, findings, appendix, insights |

Cell composition across all 142 tables: **15.84 MB string cells, 1.69 MB numeric cells.**
The payload is a string-encoding problem, not a data-volume problem.

The largest single table is `[Finalizable Object Analysis] Top finalizer queue entries by
estimated retained size` — 6.60 MB, 68,576 instance rows over only **118 distinct types**:

| Column | MB | Cardinality |
|---|---:|---:|
| Type Name | 1.819 | 118 |
| Address | 1.046 | 68,576 |
| Generation | 0.523 | 3 |
| Disposed | 0.395 | 4 |
| Disposed Field Found | 0.392 | 3 |
| Exact? | 0.380 | 2 |
| Critical Finalizer | 0.329 | 2 |
| IDisposable | 0.328 | 2 |
| Est. Retained | 0.287 | 1,179 |
| Shallow Size | 0.253 | 25 |

Seven of ten columns have cardinality ≤ 25 and cost 2.6 MB. Only `Address` is genuinely
high-entropy.

## Findings

### F1 — No global string interning (−13.54 MB, 48%)

The dominant finding. The same type universe is re-serialized in full across at least eight
tables: Memory Analysis *Top types* (13,884 rows), GC Generation *Per-type generation
profiles* (14,003), Leak Candidate *Top leak candidates* (14,003), Object Shape ×4 (22,054
total), Allocation Pattern ×2 (13,865). Plus `hops` and `subscriberDetails`, which are also
type names.

Per-column dictionaries recover only 5.39 MB because they cannot see across tables. A single
payload-level pool captures the cross-table repetition and measured **−13.54 MB** with a pool
of 16,628 entries costing 1.39 MB.

Encoding: add `strings: string[]` at the payload root; a string-typed column whose header is
marked interned carries integer indices into that array instead of literals. The header
already declares `type`, so there is no ambiguity between an index and a value.

Applies to: table string columns, `subgraphs[].hops`, `eventLeakInstanceCards[].publisherType`
/ `.eventFieldName`, `subscriberDetails[].type` / `.methodName`.

One caveat measured during prototyping: interning must be gated on *payoff*, not merely on
"appears twice". A naive `freq >= 2` rule pulled ~54,000 near-unique finalizer-queue addresses
into the pool, inflating it for no gain. Gate on `freq * (len - indexLen) > len`.

### F2 — `{"values":[…]}` row wrapper (−1.84 MB)

`CompactRow` ([AnalyzerDetailSection.cs:55](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs#L55))
serializes as `{"values":[…]}`. At 11 bytes of pure syntax × 175,158 rows that is 1.84 MB.

**The client already handles the fix.**
[report.renderers.sections.js:326](../../src/DumpDetective.Reporting/Templates/report.renderers.sections.js#L326)
reads:

```js
const rows = Array.isArray(ct.rows) ? ct.rows.map(function (r) {
  return Array.isArray(r.values) ? r.values : (Array.isArray(r) ? r : []);
}) : [];
```

So emitting bare arrays is a producer-only change with zero client work.

### F3 — Object Shape Analysis emits the same rows twice (−1.78 MB raw)

[ObjectShapeSectionBuilder.cs:48-72](../../src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs#L48-L72)
emits four tables with **identical 16-column schemas**:

| Table | Rows |
|---|---:|
| Reference-heavy types | 4,697 |
| Balanced types | 5,601 |
| Value-heavy types | 729 |
| **Gen2-retained types** | **11,027** |

4,697 + 5,601 + 729 = **exactly 11,027**. Verified: all rows of the three narrow tables are
present in the Gen2-retained table, matching on every shared column (100% key containment;
~99.6% exact value match, the remainder being float formatting drift between the two emit
paths — itself worth a look). The three tables are a pure partition of the fourth on its
existing `Category` column.

Emit the Gen2-retained table only and let the client derive the three views by filtering
`Category`. Post-interning this is worth −0.48 MB, since F1 already collapses the repeated
type names.

### F4 — `subscriberDetails` object duplication (−0.28 MB)

`eventLeakInstanceCards[].subscriberDetails` holds 5,835 entries with only **1,305 distinct**
`{type, methodName, count, size, sizeIsExact}` objects. Replace with indices into a
section-level `subscriberDetailPool`.

### F5 — Constant columns (−0.26 MB)

31 columns across the ≥20-row tables carry the identical value in every row — e.g.
`[String Analysis] Top duplicate strings :: Dominant Type` is `"System.String"` 15,294 times
(0.23 MB), `:: Sampling` is `"Prebuilt"` 15,294 times (0.16 MB), `[Leak Candidate] :: Severity`
is `"Info"` 14,003 times.

Hoist the value onto the header as `constant` and drop the column from the row arrays. This
requires a header↔row arity contract (a `cols` index array on the table telling the client
which headers remain positional). Lowest value-to-complexity ratio of the set; first thing to
cut if the diff needs to shrink.

### F6 — Float precision (−0.11 MB)

Percentages and ratios serialize at full double precision — `Gen2%` ships as
`83.11189368948767`. 8,204 values carry more than three decimals. Rounding to 3 dp at the
serializer costs nothing analytically (these are display percentages) and saves 0.11 MB.

### F7 — Transport is uncompressed (−26 MB)

The payload is embedded as raw JSON text in a `<script type="application/json">` tag
([report.html:45](../../src/DumpDetective.Reporting/Templates/report.html#L45)). It is ~92%
compressible.

Measured, and **verified working from `file://` in Chrome** (built a real 2.16 MB HTML,
headless load, decompress + `JSON.parse` of 12.6 MB in **53 ms**):

| Payload | Plain | gzip | gzip→base64 | brotli | brotli→base64 |
|---|---:|---:|---:|---:|---:|
| Baseline | 29.43 | 2.38 | **3.18** | 1.76 | 2.34 |
| Restructured (F1–F6) | 12.15 | 1.62 | **2.16** | 1.48 | 1.97 |

**gzip, not brotli.** Verified by constructing `DecompressionStream` directly in Chrome:

```
gzip=OK  deflate=OK  deflate-raw=OK  brotli=NO  br=NO  zstd=NO
```

The WHATWG Compression Streams spec never standardized brotli. Using it would mean bundling an
inflate implementation, which costs more than the 0.19 MB it would save.

`DecompressionStream` is not secure-context-gated, so `file://` works — confirmed empirically
above, which matters because these reports are opened as local files with no HTTP server.

## What was ruled out

**A pivot to one wide per-type table.** Checked all 103 column pairs across the ten
type-keyed tables with ≥1,000 rows for cross-table duplicated facts (≥99.5% agreement on
shared keys). Every match was a low-cardinality boolean or category column — `Finalizable`,
`Category`, `Value Type`, `Array` — that F1 already collapses to a single-digit integer. The
largest genuine duplicate pair is 0.076 MB. The numeric columns across these tables are
distinct data. **No schema redesign is warranted**; the encoding fixes capture the whole win.

**Delta-encoding addresses.** The 68,576 finalizer-queue addresses (1.05 MB) are sorted by
retained size, not by address, so delta coding would require shipping a permutation array
larger than the saving.

**Any form of row capping, sampling, or top-N truncation.** Out of scope by project rule.

## Combined result

| Stage | MB | Delta |
|---|---:|---:|
| baseline | 28.17 | |
| F2 bare row arrays | 26.34 | −1.84 |
| F6 3 dp floats | 26.23 | −0.11 |
| **F1 global string pool** | **12.69** | **−13.54** |
| F4 subscriberDetails pool | 12.41 | −0.28 |
| F5 constant-column hoisting | 12.15 | −0.26 |
| F3 drop Object Shape partitions | 11.58 | −0.48 |
| F7 gzip + base64 | **2.05** | −9.53 |

**HTML file: 29.9 MB → ~2.6 MB (11×).** Transport alone (F7, no model changes) gets to
~3.7 MB.

Secondary benefit, and arguably the more important one on a 25 GB-dump report: `JSON.parse`
drops from 145 ms to 75 ms and peak browser heap roughly halves.

## Sequencing

### Phase 1 — F7 transport (selected first) — done

Self-contained, touches no analyzer or section builder, delivers 8× on its own, and is
independently revertable.

**Measured on the reference dump, full report (all 36 analyzers): 29.9 MB → 3.69 MB (8.1×).**
Verified end-to-end in headless Chrome from `file://` — decompresses and renders with no
content loss. Committed as `6bf41022`.

- Producer: [HtmlReportRenderer.Render](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs#L37-L88)
  gzips `payloadJson` and base64s it into the template.
- Template: [report.html:45](../../src/DumpDetective.Reporting/Templates/report.html#L45) —
  the payload script tag needs a type/marker distinguishing compressed from plain.
- Client: [report.main.js:7](../../src/DumpDetective.Reporting/Templates/report.main.js#L7)
  branches on that marker, `atob` → `Uint8Array` → `DecompressionStream('gzip')` →
  `TextDecoder` → `JSON.parse`.

Two constraints this phase must respect:

- **Payload read becomes async.** `report.main.js` currently reads and parses synchronously at
  module scope. The decompression path is promise-based, so bootstrap has to be sequenced
  behind it.
- **Keep the uncompressed path.** Emit plain JSON below a size threshold and when
  pre-rendering, so small reports stay diffable and greppable, and so there is a fallback if
  `DecompressionStream` is absent. The same branch covers `per-dump-json` in trend mode.

### Phase 2 — F1 + F2 payload restructuring — done

F1 and F2 together are ~15.4 MB and share the serializer touchpoints; done as one change.

- F2: [CompactRow](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs#L55) now
  carries `[JsonConverter(typeof(CompactRowJsonConverter))]`
  ([CompactRowJsonConverter.cs](../../src/DumpDetective.Reporting/Serialization/CompactRowJsonConverter.cs)),
  writing/reading the bare-array shape the client already preferred.
- F1: [ReportStringPool.cs](../../src/DumpDetective.Reporting/Formatters/ReportStringPool.cs)
  builds a payload-level `strings` pool from every string cell across
  `domains[].sections[].compactTables` (and trend mode's `trendAnalyzerSections`), gated by a
  profitability check (`occurrences * (literalCost - indexCost) > literalCost`) rather than a
  fixed length/frequency cutoff — this is what lets high-frequency short tokens like `"No"`
  (259,887 occurrences) qualify for pooling alongside long type names, which a naive
  length-based gate would have excluded. Applied in
  [HtmlReportRenderer.Render](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs),
  skipped for pre-rendered reports (their tables are already static server-rendered HTML, so
  the client-side pool-resolution path below never runs for them).
- Client resolution: `report.renderers.shared.js` (`setStringPool`/`resolveStr`) and
  `report.renderers.sections.js` (`resolvePooledRow`) resolve pooled cells back to literals
  *before* they reach display, sort, search, or filter — scoped to non-numeric columns only,
  since the producer never pools a cell in a `number`/`bytes`/formatted column. "Download JSON"
  (`report.ui.actions.js`) resolves the pool on a clone before export, so the downloaded file
  reads exactly as it did before F1 existed.
- **Scope cut, disclosed:** pooling covers `compactTables` cells only, not
  `rootOwnedSubgraphGroups[].subgraphs[].hops` or `eventLeak*Cards[].subscriberDetails`/
  `.publisherType`/`.eventFieldName` as F1 originally described. Those fields are typed
  `string`/`IReadOnlyList<string>` in the object model (not `object?[]` like `CompactRow.Values`),
  so pooling them would mean changing those types everywhere they're constructed and consumed.
  `compactTables` is the dominant cost (21.44 MB of the original 28 MB) and captures the large
  majority of F1's win; the remaining ~4.5 MB across hops/subscriberDetails/event-leak fields is
  a follow-up if it's worth the type-model churn.

**Measured on the reference dump, full report: 29.9 MB → 2.81 MB (10.6×)** — noticeably better
than Phase 1's gzip-only 3.69 MB, since pre-compaction reduces the raw redundancy gzip has to
encode. Verified end-to-end in headless Chrome: rendered DOM shows resolved literal type names
(`System.Data.DataColumn` × 20, `>No<` × 87, `>Yes<` × 205) with no leaked pool indices, and
row/table counts match the Phase 1 baseline exactly.

### Phase 3 — F3 + F4 + F6 structural cleanups — done

Independent of each other and of Phase 2.

- F3: [ObjectShapeSectionBuilder.cs](../../src/DumpDetective.Reporting/SectionBuilders/ObjectShapeSectionBuilder.cs)
  no longer emits the Reference-heavy/Value-heavy/Balanced tables. Source inspection of
  [ObjectShapeAnalyzer.cs](../../src/DumpDetective.Analysis/Analyzers/ObjectShapeAnalyzer.cs)
  showed `TopGen2RetainedTypes` is built as the literal concatenation of the other three lists
  (`gen2RetainedCandidates.AddRange(refHeavyCandidates); ... AddRange(valHeavyCandidates); ...
  AddRange(balancedCandidates);`) — the same `TypeShapeProfile` object references, not a
  re-derived copy. That supersedes the "~0.4% value drift" this doc originally flagged as worth
  investigating: there is no second emit path to drift from, so the drift I measured earlier was
  an artifact of the ad hoc comparison script, not a product bug. The existing per-table search
  box (whole-row substring match) already reproduces a per-category view by typing e.g.
  `ReferenceHeavy` into the filter, so no new client-side filtering UI was needed.
- F4: [EventLeakSubscriberPool.cs](../../src/DumpDetective.Reporting/Formatters/EventLeakSubscriberPool.cs)
  dedupes `eventLeakInstanceCards[].subscriberDetails` the same way F1 dedupes strings — entries
  occurring once stay inline (a lone index reference costs more than the object it would
  replace), repeated entries become indices into a new section-level `subscriberDetailPool`. The
  element type change (`IReadOnlyList<SubscriberDetailEntry>?` → `IReadOnlyList<object>?`) is
  carried by
  [SubscriberDetailListJsonConverter.cs](../../src/DumpDetective.Reporting/Serialization/SubscriberDetailListJsonConverter.cs);
  client resolution lives at the one render call site in `report.renderers.sections.js`.
- F6: folded into
  [CompactRowJsonConverter.cs](../../src/DumpDetective.Reporting/Serialization/CompactRowJsonConverter.cs) —
  `double`/`float` cells round to 3 decimal places at write time. Scoped to compact-table cells
  only (not every double in the document, e.g. confidence composites elsewhere) since that's
  where the measured precision waste actually lives (`Gen2%` at full `83.11189368948767`
  precision, etc.) and it's a single already-owned converter rather than new touchpoints.

**Measured on the reference dump, full report: 29.9 MB → 2.65 MB (11.3×)**, a further ~5.7% off
Phase 2's 2.81 MB. Verified end-to-end in headless Chrome: the Object Shape section shows only
the Gen2-retained table (11,027 rows; no "Reference-heavy types"/"Balanced types" table markup
anywhere), and all 7,815 rendered subscriber-detail type cells resolved to real type names with
zero leaked pool indices.

### Phase 4 — F5 constant-column hoisting

Optional. Deferred because it needs a header↔row arity contract for the smallest payoff in
the set.

## Verification

Each phase must hold these invariants:

- Round-trip equality: the document the client parses is deep-equal to the pre-transform
  document, modulo F6's declared 3 dp rounding.
- Rendered HTML for the reference dump is unchanged (table row counts, cell text, sort order).
- The report opens and renders from `file://` with no HTTP server.
- No row, column, or table is dropped that was present before — F3 removes tables whose
  content is provably still present in a retained table.
