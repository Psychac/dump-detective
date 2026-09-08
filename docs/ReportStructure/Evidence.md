# Evidence — the redesign, pressure-tested against a real report

## Status

Measurement record, not a proposal. Every number here was produced by extracting and analyzing a
real shipped report; nothing is estimated or asserted. This exists so the three baseline docs
([README.md](README.md)) rest on measurement rather than opinion, and so ideas that **failed**
under test are recorded as failed instead of quietly surviving.

**Artifact:** `D:\DUmps\21-04\w3wp.exe_260421_175618.html` — 26.2 MB HTML, generated from a ~25 GB
`w3wp.exe` dump, 573.95 s analysis, schema 2.1, single-dump mode. Measured 2026-09-06.

**Method:** extract the base64+gzip payload from `<script id="report-json">`, inflate, analyze. No
dump was loaded; the cost of this measurement was one file read. Reproduction in
[§8](#8-reproduction).

---

## 1. Headline

| | Appendix A baseline (3.35 GB dump) | This artifact (~25 GB dump) | Ratio |
|---|---|---|---|
| HTML file | 3.7 MB | **26.2 MB** | 7.1× |
| gzip payload | 2.54 MB | **20.2 MB** | 8.0× |
| **Inflated JSON, `JSON.parse`d on every open** | 30.9 MB | **183.9 MB** | **6.0×** |
| Table rows | 175,158 | **4,349,595** | 24.8× |
| Table cells | 1,821,559 | **43,481,228** | 23.9× |
| Sections | 37 | 37 | — |
| Lead findings | 60 | 33 (10 Critical, 15 Warning, 8 Info) | — |

The report the two clean-slate docs were written against was **not the bad case.** It was six times
better than what the tool produces on a large dump. Every payload argument in those docs is
understated by roughly that factor.

---

## 2. The single most important number

One table holds **95% of the entire report**:

| Section | Table | Rows | `rowLimit` | Bytes | Displayed |
|---|---|---|---|---|---|
| B6 Finalizable Objects | "Top finalizer queue entries by estimated retained size" | **4,248,457** | 20 | **215,053,432** | **0.0005%** |

215 MB is serialized, gzipped, base64'd, shipped, inflated and `JSON.parse`d on every open, so that
twenty rows can be drawn. The other 132 tables in the report total ~11 MB between them.

B6's whole section is 215,063,350 bytes of a 225,996,283-byte `domains` block. Everything else —
every analyzer, every finding, every chart, all 36 other sections — is 4.8%.

This is [ReportFormatCleanSlate](ReportFormatCleanSlate.md)'s central thesis, at 62× the magnitude
it was argued at. It is not a payload problem that an encoder can fix; **no encoding makes 4.2
million rows the right thing to ship in order to display 20.**

---

## 3. Ideas that were tested

### 3.1 FALSIFIED — "P0 is a pure win": turn on the string pool and the compact row converter

[ReportFormatCleanSlate §10](ReportFormatCleanSlate.md#10-phased-migration) makes **P0a** the first
migration step: populate `strings`, land `CompactRowJsonConverter` on the HTML path, worth ~11.8 MB
of the 30.9 MB baseline. Its [§11 open question 1](ReportFormatCleanSlate.md#11-open-questions)
asks whether these are "never wired up" or "a regression."

**Both are already on in this artifact, and it is still 183.9 MB.**

- `strings` is populated — **14,791 pooled entries**, 1.18 MB.
- Rows are JSON arrays, not `{"values":[…]}` objects — all 133 tables. The compact converter is in
  effect.

So the answer to the open question is neither: they work, they shipped, and they are not where the
problem is. P0 remains worth keeping — it is real compression — but it must be demoted from "the
first and safest win" to "a rounding error against the editorial problem." A migration plan that
leads with P0 spends its first phase moving 6% while the reader still waits on 184 MB.

**Consequence for the baseline docs:** ReportFormatCleanSlate §10's phase ordering is wrong.
Editorial disclosure (P1) must come first; it is worth ~95% on this artifact, and P5's segmented
envelope only matters for what survives that cut.

### 3.2 SURVIVES — nulls are null; presentation does not belong in data

Counted across all 43,481,228 cells:

| Cell value | Occurrences |
|---|---|
| `"No"` | 9,131,167 |
| `"N/A"` | 8,469,660 |
| `"Gen 2"` | 4,190,916 |
| `"System.Data.DataColumn"` | 4,083,856 |
| `"Yes"` | 3,708,220 |
| `"—"` | (pooled, in the same band) |

**21,337,123 cells — 49.1% of every cell in the report — are `"Yes"`, `"No"`, `"N/A"` or an
em-dash.** Nearly half the payload is booleans and nulls spelled out in English, because the C#
side formats for the text renderer and every other renderer inherits the artifact.

The string pool does not fix this. It makes each occurrence an integer index instead of a literal,
which is why the pool exists and why it helps — but **it compresses the mistake rather than
removing it.** A boolean column shipped as pooled indices into `"Yes"`/`"No"` still cannot be
filtered as a boolean, aggregated, or rendered as anything else.

This is the strongest possible support for
[ReportSystemVision §5.3](ReportSystemVision.md#53-measure-semantics-drive-everything-downstream):
typed measures, nulls null, booleans booleans, formatting owned by the presenter.

### 3.3 SURVIVES — the entity table, and dedup as a side effect

`"System.Data.DataColumn"` occurs **4,083,856 times as a cell value** in a single report. It is one
entity. Under an entity table it is one row and four million integer references — which the string
pool already partly achieves, but without any of the semantics: today it is an opaque pooled
string, not a type you can pivot on, click, or join across artifacts.

### 3.4 SURVIVES — entity identity is inconsistent today, so joining is impossible without it

Across 133 tables there are **287 distinct column names for 655 columns.** The *same* entity kind —
a managed type — is named at least 22 different ways:

```
Type (52×)   Type Name (6×)   Class (2×)   Types (2×)   Top Types   Live Types   Retained types
Element Type   Exception Type   Value Type   Publisher Type   Subscriber Type   Declaring Type
Dominant Type   Target Type   Root Type   Task Type   Result Type   Inner Type   Wait Type …
```

Some of those are genuine *roles* in a relation — `Publisher Type` and `Subscriber Type` are two
different subjects of one fact, which is exactly what `subjects: EntityRef[]` with roles expresses.
But `Type` / `Type Name` / `Class` / `Types` are one thing under four names, and no consumer can
know that. There is no join key in the current format; there is a naming convention that is not
kept.

### 3.5 SURVIVES — measure semantics, because the existing type tags are already broken

`CompactHeader` carries a `type` tag, and its stated intent is that formatting is the client's job.
The actual distribution of that field across 655 columns:

| `type` value | Columns |
|---|---|
| `string` | 302 |
| `number` | 254 |
| `bytes` | 94 |
| `percent` | 2 |
| `int` | 1 |
| `double` | 1 |
| **`objects/MB`** | **1** |

`objects/MB` is a *unit* that leaked into the *type* slot. `int` and `double` are CLR types, not
display types. And the 302 `string` columns include `Address` (an address), `Generation` (an
enum), and every boolean column in the report.

An unenforced, undocumented, open-ended tag is worse than none, because consumers write code
against the three values they happened to see. This is why
[ReportSystemVision §5.3](ReportSystemVision.md#53-measure-semantics-drive-everything-downstream)
makes unit and semantics a closed, required pair, and
[§17.2](ReportSystemVision.md#172-data-gates) gates it.

### 3.6 SURVIVES — claims need counter-evidence, and measures need to be named apart

B6 presents these two numbers, one directly above the other:

| Where | Value |
|---|---|
| `leadFinding.summary` | "Finalizer queue holds 42,48,457 objects **retaining ~9.15 GB**" |
| `keyMetrics.total_finalizable_memory` | **889.03 MB** |

I recomputed both from the 4,248,457 rows:

```
sum of "Est. Retained" column = 9,820,293,241 bytes = 9.15 GiB   ← the lead
sum of "Shallow Size" column  =   919,733,040 bytes =  877 MiB   ← ~the key metric
```

**Neither is wrong.** They measure different things — retained-subgraph bytes versus shallow object
bytes — and the report presents them adjacently with nothing saying so. A reader sees a 10× gap
between two authoritative numbers in the same section and has no way to reconcile them.

Worse, the 9.15 GB is a **sum of per-object retained sizes over 4.2 million objects**, so shared
retained subgraphs are counted once per dominator. On a `DataSet`/`DataTable`/`DataColumn` graph —
which is exactly what this heap is — that overlap is large. The number is very likely a
substantial overcount, and nothing in the report says it might be.

This is the failure mode [ReportSystemVision §1.3](ReportSystemVision.md#13-the-three-ways-a-generated-report-fails)
calls **F1**, and it is precisely what
[§6.3](ReportSystemVision.md#63-a-claim-carries-its-own-refutation) is designed to catch: the
counter-evidence for the 9.15 GB claim was *already in the same section*, and a claim obliged to
carry its own refutation could not have shipped without it.

### 3.7 SURVIVES — formatting belongs to the presenter, and reports must be deterministic

**33 narrative strings in this report use Indian digit grouping.** Examples, verbatim:

```
"Finalizer queue holds 42,48,457 objects retaining ~9.15 GB."
"Type 'System.String' contributes 90,52,241 incoming references…"
"…4,90,728 sampled out of ~1,91,27,307 total…"
"…breadth cap 20,00,000, depth cap 20."
"42,60,241 Gen2 objects (877.3 MB) are of finalizable types."
```

Root cause: **671 `:N0` / `ToString("N0")` sites across 126 files, none of which specifies a
culture.** They format with `CurrentCulture`, so report *content* depends on the regional settings
of the machine that ran the analysis.

Two consequences:

1. The same dump analyzed on two machines produces two different reports. Golden-diffing,
   byte-identical regression baselines and the determinism requirement in
   [ReportSystemVision §15](ReportSystemVision.md#15-schema-and-versioning) are all impossible today.
2. It is a live defect for anyone outside the report author's locale.

It also demonstrates the deeper point: these numbers are baked into prose **in C#**, so no
presenter can reformat them. Under the baseline the number would travel as a typed measure and be
formatted at render, and this bug class would not exist.

---

## 4. Findings with no home in the current design

Things the measurement surfaced that none of the three baseline docs anticipated.

### 4.1 Section skew is extreme and unbounded

Not "some sections are large" — one section is **95%**, and the distribution is a power law:

| Section | Bytes | Share |
|---|---|---|
| B6 Finalizable Object Analysis | 215,063,350 | 95.2% |
| A5 GC Root Analysis | 3,273,303 | 1.4% |
| A7 String Analysis | 2,846,604 | 1.3% |
| D4 Event Leak Analysis | 1,708,957 | 0.8% |
| A1 Leak Candidate Analysis | 643,463 | 0.3% |
| all 32 others | ~2.5 MB | 1.1% |

**Design consequence:** a per-section byte budget is not enough, because the offender is whichever
analyzer happens to meet a pathological heap. The gate must be *per widget, on row count relative
to what the claim cites*, and it must fail the build rather than warn. A budget expressed as "the
report should be under N MB" would have passed every artifact until this one.

### 4.2 The report has no mechanism to say "this heap is pathological"

4.2 million finalizable objects is not a normal heap; it is the finding. Yet it is expressed as a
table with 4.2 million rows — the tool responds to an extreme measurement by shipping extreme data,
rather than by saying *"this measurement is extreme."* That is
[T6 — comparison is the unit of judgment](ReportSystemVision.md#t6--comparison-is-the-unit-of-judgment)
arriving from a direction the doc did not anticipate: a reference class is not only how you
*describe* a number, it is how you decide **how much evidence to ship** for it.

**Adopted into the design:** the volume of evidence a widget discloses by default should scale with
how far the measurement sits from its reference — not with how many rows the analyzer produced.

### 4.3 Lead findings are sparse relative to sections

33 lead findings across 37 sections, of which 10 are Critical — on a heap this pathological. Four
sections carry no lead at all. The claim-per-widget requirement in
[§17.1](ReportSystemVision.md#171-editorial-gates) would have to generate ~133 claims (one per
table) from analyzers that currently produce 33 conclusions.

**Open risk this raises:** the editorial contract assumes analyzers have more to say than they
currently say. If they do not, the contract produces 100 claims of the form "here is a table," which
is worse than 33 real ones. This needs testing before P1 is committed — see
[§7](#7-what-this-does-not-yet-establish).

---

## 5. What changes in the baseline docs

| Doc | Change | Why |
|---|---|---|
| ReportFormatCleanSlate §10 | Demote **P0** below **P1**; stop describing it as the leading win | §3.1 — both P0 optimizations already ship and the report is still 183.9 MB |
| ReportFormatCleanSlate §11 Q1 | **Answered and closed** — `strings` and `CompactRowJsonConverter` are both live on the HTML path | §3.1 |
| ReportFormatCleanSlate Appendix A | Note that it is the *mild* case, ~6× understated | §1 |
| ReportSystemVision §9 budgets | Replace whole-report byte budgets with a per-widget rule tied to cited rows | §4.1 |
| ReportSystemVision §5.4 | Add: disclosure volume scales with distance from reference, not with row count | §4.2 |
| ReportSystemVision §15 | Determinism is currently *violated*, not merely unspecified | §3.7 |
| All three | Payload figures should cite this artifact, not only Appendix A | §1 |

---

## 6. Defects found (independent of the redesign)

| # | Defect | Evidence | Fix |
|---|---|---|---|
| D1 | Report content is locale-dependent; 33 narrative strings use Indian digit grouping | §3.7 | Set an invariant culture once at CLI startup — one change fixes all 671 sites |
| D2 | `leadFinding` states 9.15 GB retained beside a 889 MB key metric, with no distinction and no overlap caveat | §3.6 | Name the two measures apart; caveat the double-count |
| D3 | `CompactHeader.type` carries `objects/MB`, `int`, `double` alongside the real tags | §3.5 | Close the vocabulary; gate it |
| D4 | 215 MB shipped to render 20 rows | §2 | The redesign |

D1 is the cheapest and most clearly wrong; it is also a prerequisite for any golden-report test.

---

## 7. What this does NOT establish

Stated so the evidence is not over-read:

1. **One artifact.** Findings about *shape* (presentation-in-data, entity naming, type tags,
   culture) are structural and generalize. Findings about *magnitude* (95% in one section) are
   specific to a heap with 4.2M finalizable objects. Both matter, differently.
2. **The claim graph is not tested here.** Nothing above proves that good claims with real
   counter-evidence and falsification tests can be authored at scale — §4.3 raises a genuine risk
   that they cannot. That probe is still outstanding and is the next thing to run.
3. **No visual or interaction hypothesis was tested.** Nothing here validates the widget
   vocabulary, the four surfaces, or the query algebra.
4. **Single-dump only.** No trend artifact was analyzed, so the session model is unmeasured.

---

## 8. Reproduction

```python
import re, base64, gzip, json, io, collections

SRC = r'D:\DUmps\21-04\w3wp.exe_260421_175618.html'
html = io.open(SRC, encoding='utf-8').read()
b64  = re.search(r'id="report-json"[^>]*>(.*?)</script>', html, re.S).group(1).strip()
data = gzip.decompress(base64.b64decode(b64))
print(f'b64={len(b64):,} gz={len(base64.b64decode(b64)):,} inflated={len(data):,}')

r = json.loads(data)['report']
pool = r['strings']

occ, cells = collections.Counter(), 0
for dom in r['domains']:
    for sec in dom.get('sections') or []:
        for t in sec.get('compactTables') or []:
            hdrs = t.get('headers') or []
            for row in t.get('rows') or []:
                for i, v in enumerate(row):
                    cells += 1
                    if isinstance(v, int) and 0 <= v < len(pool) \
                       and i < len(hdrs) and hdrs[i].get('type') == 'string':
                        occ[pool[v]] += 1

pres = sum(c for s, c in occ.items() if s in {'Yes','No','N/A','—','-','True','False',''})
print(f'cells={cells:,} placeholders={pres:,} ({pres/cells*100:.1f}%)')
```

Set `PYTHONIOENCODING=utf-8` on Windows — several cells contain U+2014.
