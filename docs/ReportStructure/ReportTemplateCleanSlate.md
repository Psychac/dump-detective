# Report Template Layer — Clean-Slate Redesign

## Status

Design proposal. Nothing here is implemented. Sibling to
[ReportFormatCleanSlate.md](ReportFormatCleanSlate.md), which defines *what* the report contains;
this defines *how it is built and styled*. The two share a widget vocabulary — the components in
§3 below are the same closed set as ReportFormatCleanSlate §2, seen from the template side.

## Thesis

**The template layer has a design system and doesn't use it, and has module files that don't own
anything.**

- 98 design tokens are defined, and **954 hardcoded color literals** sit alongside them.
- Five orthogonal body-level modes (`report-style-v2`, `high-contrast`, `report-density-*`,
  `reading-mode-*`) are implemented as **hand-duplicated rule blocks** rather than token overrides,
  so every mode multiplies the stylesheet instead of re-pointing it.
- 21 JS files are flattened into one closure scope by a regex-based bundler, and their names
  describe neither ownership nor layer — a 60 KB file called "header" holds the health scorecard
  and the executive summary.

The result is 154 KB of CSS and 303 KB of JS to render a document whose widgets number about a
dozen. Full measurements in [§8](#8-measured-analysis).

---

## 1. What is already good

Worth stating first, because a clean slate should not throw these away:

| Keep | Evidence |
|---|---|
| Programmatic DOM construction | 133 `createElement` / 406 `textContent` against only **3** `innerHTML` uses. The output is structurally safe; do not regress to template strings. |
| Real accessibility work | 91 `aria-*` attributes, 24 `role=` assignments, skip links, `aria-live` regions, `aria-expanded` on every collapsible. |
| Near-zero global mutable state | Exactly one module-level `let` (`_stringPool`). State is passed, not ambient. |
| `dom.js` helper shape | `el`, `t`, `nvl`, `isInside`, `createAriaLive` — a small, correct builder surface. Keep the idea, fix the ownership. |
| `high-contrast` and `reading-mode` | Real features with persistence. The *mechanism* is wrong (§2.4), the *intent* is right. |
| Motion respect | `report.ui.motion.js` already gates animation. |

---

## 2. Design system

### 2.1 Three token layers, one rule

```
primitive   --dd-blue-600, --dd-gray-050, --dd-size-14, --dd-space-unit
     ↓      (raw scales — the ONLY place a literal value may appear)
semantic    --dd-surface, --dd-text-muted, --dd-severity-critical-bg, --dd-border-subtle
     ↓      (role-named; what component CSS is allowed to reference)
component   --dd-table-row-height, --dd-callout-pad
```

**The rule:** a color, size, or duration literal outside the primitive layer is a build failure.
Today's ratio is 98 tokens to 954 literals; the target is 954 → 0.

### 2.2 Color and theme

`:root { color-scheme: light dark; }` and every semantic token defined once with `light-dark()`:

```css
--dd-surface:      light-dark(var(--dd-gray-000), var(--dd-gray-950));
--dd-text:         light-dark(var(--dd-gray-900), var(--dd-gray-050));
--dd-border-subtle:light-dark(var(--dd-gray-200), var(--dd-gray-800));
```

Dark mode then costs **zero** duplicated rule blocks — the current stylesheet has no
`prefers-color-scheme` at all, and adding one under today's structure would mean re-authoring 954
literals in a second place.

### 2.3 Severity scale

One scale, five steps (`critical`, `warning`, `info`, `ok`, `neutral`), each a triplet
(`-bg`, `-border`, `-fg`), contrast-verified ≥ 4.5:1 in both schemes. Severity is expressed as a
**data attribute**, never a composed class:

```html
<article class="dd-callout" data-severity="critical">
```
```css
.dd-callout[data-severity="critical"] { … }
```

Two reasons. It keeps every class in the stylesheet statically greppable (§4.4), and it replaces
`sevCss()`-style string building with markup the CSS can address directly.

Severity must also survive color loss: an icon glyph and a text label ship with the attribute, so
the encoding is shape + label + color, never color alone.

### 2.4 Modes are token overrides, not rule sets

The five body modes today own 91 hand-written rule blocks between them. Replace all of it:

```css
[data-density="compact"]   { --dd-space-scale: 0.72; --dd-row-height: 24px; }
[data-contrast="high"]     { --dd-border-subtle: var(--dd-border-strong); --dd-text-muted: var(--dd-text); }
[data-reading-mode="forensics"] { --dd-detail-default-open: block; }
```

A mode re-points tokens; it never re-declares component rules. This is what makes modes
composable — today `high-contrast` × `density` × `reading-mode` is a combinatorial space nobody has
authored, and it shows.

Note that `report-density-compact` / `report-density-comfortable` own 25 CSS rules and are **never
set by any JavaScript** — a feature that exists only in the stylesheet. Under §2.4 it becomes two
token lines and a toggle.

### 2.5 Type and numerics

One UI stack, one mono stack, a six-step size scale. Every numeric cell, metric value, and axis
label carries `font-variant-numeric: tabular-nums` — in a report whose primary content is columns
of byte counts, misaligned digits are a legibility bug, not a nicety.

### 2.6 Breakpoints

Today: **13 distinct breakpoints** across 22 media queries, nine of them used exactly once
(1279, 1180, 1100, 1050, 900, 760, 700, 680, 640, 600, 540, 460 px). Replace with three named
tokens — `--bp-compact`, `--bp-regular`, `--bp-wide` — and container queries for widgets, which
belong to their container's width rather than the viewport's.

---

## 3. Component library

One component per widget in the format's closed vocabulary. Each is a **triple**:

```
widgets/table.js       build(spec, ctx) → HTMLElement       — no globals, no DOM lookups outside its subtree
widgets/table.css      .dd-table + BEM parts                — semantic tokens only, one file, no exceptions
fixtures/table.json    a spec fixture                        — feeds both tests and the gallery
```

| Component | Owns |
|---|---|
| `dd-table` | column store rendering, sort, filter, pagination, row citation targets |
| `dd-tree` | collapsible tree (today's `TreeWidget`) |
| `dd-chain` | root/reference hop chains with field names |
| `dd-stack` | stack frames, framework-frame folding, meta strip |
| `dd-cluster` | stack signature clusters |
| `dd-distribution` | histogram, treemap, sparkline |
| `dd-timeline` | snapshot-indexed series (trend) |
| `dd-kv-strip` | metric strips |
| `dd-callout` | lead finding, confidence band, next steps |
| `dd-entity-card` | type / thread / root cards |
| `dd-code` | copyable monospace blocks (paths, SOS commands) |

Plus four chrome modules that are *not* widgets and must stop being mixed in with them:
`shell` (nav, rails, skip links), `search`, `filters`, `toc`.

### 3.1 Component gallery

The build emits a second artifact, `gallery.html`, rendering every component against its fixtures
in every mode (light/dark × density × contrast). Today the only way to see a styling change is to
analyze a multi-GB dump and open a 3.7 MB file. The gallery makes the template layer developable in
isolation and gives visual-regression tests something stable to shoot.

---

## 4. Code architecture

### 4.1 Real modules, real bundler

Today [HtmlReportRenderer.BuildInlinedBundle](../../src/DumpDetective.Reporting/Formatters/HtmlReportRenderer.cs)
concatenates 18 files, strips `export` with regexes, and deletes `Dom.` / `R.` / `UI.` prefixes
with `Regex.Replace` — so all 119 top-level declarations land in one shared closure and the *file
append order* decides which definition wins.

That is not hypothetical: `formatBytes` is defined in both `report.dom.js` and
`report.renderers.sections.js` with **different output** (`"1 KB"` vs `"1.0 KB"`; `"0 B"` vs `""`
for non-numeric), and `sections.js` is appended later, so it silently wins for every caller.

Replace with ESM + a real bundler emitting one minified IIFE. A duplicate export becomes a build
error rather than a coin flip.

### 4.2 Module graph

```
bootstrap.js        payload decode → state init → shell mount → lazy segment wiring
  ├── core/dom.js       el(), t(), fragment helpers            (today's report.dom.js — keep)
  ├── core/format.js    bytes, count, percent, duration, hex   (SINGLE source; kills 4.1)
  ├── core/state.js     one store: filters, expansion, mode, theme; URL-hash sync
  ├── core/events.js    delegated listeners at one root
  ├── core/segments.js  lazy inflate + cache (format doc §7)
  ├── widgets/*.js      the eleven components above
  └── chrome/*.js       shell, search, filters, toc
```

Hard rules:

1. A widget never reaches outside its own subtree; it receives `ctx` and returns an element.
2. No `innerHTML`. Currently 3 uses — finish the job.
3. Cross-widget communication goes through `state`, never direct DOM queries.
4. One formatting implementation, imported everywhere.

### 4.3 Events

52 `addEventListener` sites today. Widgets attach nothing directly; one delegated listener per
event type at the shell root dispatches on `data-action`, so lazily inflated content (format doc
§7) needs no re-binding — the current model requires re-wiring after every dynamic insert, which
is exactly where interaction bugs come from.

### 4.4 Statically analysable CSS

824 classes are defined; **255 have no literal reference** in JS or HTML. Some of those are
dynamically composed (`'detail-table-' + s`), which is the real problem: *you cannot tell dead from
live*. Forbid composed class names — use fixed classes plus data attributes (§2.3) — and a
dead-class check becomes a trivial CI gate rather than an impossible audit.

---

## 5. CSS architecture

### 5.1 Cascade layers

```css
@layer tokens, base, layout, widgets, chrome, utilities, print;
```

Layer order resolves precedence structurally, which removes the reason `!important` exists. Today:
**52 `!important` declarations** and a 103-character selector
(`body.high-contrast .header-card, body.high-contrast .section-card, …`) — both symptoms of a
cascade fight that layers end.

### 5.2 One component, one file

`.analyzer-section` is currently styled across `report.base.css`, `report.detail.css`, **and**
`report.header.css`; `.detail-block` across two files. The current split is by *page region*
(base / header / body / detail / findings / utilities), which guarantees a component's styles
scatter. Split by component instead — the same axis as §3.

### 5.3 Naming

Strict BEM under one prefix: `dd-<component>__<part>`, modifiers as data attributes. 602 of 824
classes are already BEM-ish; this makes it total and adds the prefix so report CSS can never
collide with anything embedded alongside it.

### 5.4 Selector budget

43 selectors currently have four or more compound parts, one has seven. Cap at three, enforced by
a stylelint rule in CI.

---

## 6. Accessibility

Preserve everything in §1 and add what the current structure can't express:

- Focus management on lazy inflate — when a segment expands, focus moves to its heading.
- `prefers-reduced-motion` honored at the token layer (`--dd-motion-duration: 0ms`), not per-rule.
- Contrast verified per severity token pair in both schemes, as a test (§7), not a review.
- Keyboard model documented once and implemented in `chrome/keyboard.js`: arrows move between
  sections, `/` focuses search, `Enter` expands, `Escape` collapses — today those key handlers are
  asserted by *string-matching the JS source* (§7).

---

## 7. Testing

The current suite has 68 assertions in
[HtmlRendererCssTests](../../tests/DumpDetective.Tests/Integration/HtmlRendererCssTests.cs),
several of which match **literal JavaScript source text** inside the bundle:

```
"main.appendChild(actionQueue);"
"function syncCollapsibleAria"
"if (ev.key === 'ArrowLeft'"
```

Those break on any refactor and guarantee nothing about behavior. Replace with:

| Layer | Test |
|---|---|
| Component | render each widget from its fixture, assert DOM shape + ARIA + data attributes |
| Design system | zero color literals outside primitives; contrast ≥ 4.5:1 per severity pair in both schemes; no `!important`; selector depth ≤ 3; no dead classes |
| Bundle | no duplicate exports; no `innerHTML`; size budgets (§9) |
| Visual | gallery snapshots per mode combination |
| Integration | one golden full-report render, structural not textual |

---

## 8. Measured analysis

Baseline for `src/DumpDetective.Reporting/Templates/`, measured 2026-09-04.

### 8.1 JS

18 bundled files, 303 KB inlined, 119 top-level declarations sharing one closure scope.

| File | Bytes | Top-level decls | Note |
|---|---|---|---|
| `report.renderers.sections.js` | 64,689 | 11 | format helpers + treemap + section assembly + correlation timeline + `renderTrendDumpGroups` |
| `report.renderers.header.js` | 60,309 | **3** | `buildHeader`, `buildHealthScorecard`, `buildExecutiveSummary` — 20 KB per function |
| `report.renderers.panels.js` | 41,873 | 9 | appendix, incident context, action queue, forensics rail, **and** search/filter bars |
| `report.ui.js` | 24,024 | 2 | `buildSidebar`, `setupInteractivity` — the leftover bucket, beside eight `report.ui.*.js` siblings |
| `report.renderers.blocks.js` | 22,460 | 5 | |
| `report.renderers.nav.js` | 22,048 | 10 | |
| `report.renderers.charts.js` | 21,818 | 23 | |
| `report.renderers.shared.js` | 10,652 | 20 | includes `buildTreeWidget` — a widget living in "shared" |
| 10 smaller files | 36 KB | 36 | |
| `report.renderers.js` | 380 | 0 | orphan |

Duplicate top-level declarations across the flattened scope: **`formatBytes`** (see §4.1).

Boundary violations found: search/filter bars implemented in `panels.js` while
`report.ui.search.js` and `report.ui.filters.js` exist; trend rendering inside `sections.js`;
a widget builder inside `shared.js`.

### 8.2 CSS

6 files, 154 KB, 1,138 rule blocks, 824 distinct classes.

| File | Bytes | Rules | Tokens defined | Color literals |
|---|---|---|---|---|
| `report.detail.css` | 55,689 | 424 | 2 | 437 |
| `report.body.css` | 37,035 | 249 | 0 | 169 |
| `report.header.css` | 25,460 | 163 | 10 | 115 |
| `report.base.css` | 24,589 | 151 | **97** | 135 |
| `report.utilities.css` | 13,069 | 82 | 0 | 51 |
| `report.findings.css` | 9,205 | 69 | 0 | 47 |
| **Total** | **154 KB** | **1,138** | **98** | **954** |

- `!important`: 52 (detail 26, base 15, body 11)
- Media queries: 22, across **13 distinct breakpoints**, 9 used once
- Selectors with ≥ 4 compound parts: 43; deepest: 7
- Classes with no literal reference in JS/HTML: 255 of 824
- BEM-conforming: 602 of 824
- `prefers-color-scheme`: **0**

### 8.3 Mode classes

| Body class | Rules | Set from JS? |
|---|---|---|
| `high-contrast` | 21 | yes — toggle + `localStorage` |
| `report-style-v2` | 19 | yes — deleted per format doc §8.1 |
| `report-density-compact` | 17 | **no** |
| `reading-mode-forensics` | 14 | yes — toggle + `localStorage` |
| `reading-mode-incident` | 10 | yes |
| `report-density-comfortable` | 8 | **no** |

`reading-mode-incident` / `reading-mode-forensics` is a partial, already-shipped answer to
[ReportFormatCleanSlate.md §11 question 5](ReportFormatCleanSlate.md#11-open-questions) ("who is
the default reader") — the personas exist in the UI but nothing in the *content* model varies by
them. Worth resolving in one place rather than two.

---

## 9. Budgets

| Gate | Target | Today |
|---|---|---|
| CSS, minified | ≤ 20 KB | 154 KB |
| JS, minified | ≤ 80 KB | 303 KB unminified |
| Color literals outside primitive tokens | 0 | 954 |
| `!important` | 0 | 52 |
| Distinct breakpoints | 3 | 13 |
| Selector compound depth | ≤ 3 | 7 |
| Duplicate top-level declarations | 0 | 1 (`formatBytes`) |
| `innerHTML` uses | 0 | 3 |
| CSS classes with no reference | 0 | 255 |
| Files per component's styles | 1 | up to 3 |

---

## 10. Migration

Aligned with [ReportFormatCleanSlate.md §10](ReportFormatCleanSlate.md#10-phased-migration); this
is the detail behind its **P2** and **P6**.

| Phase | Scope | Effect |
|---|---|---|
| **T0** | Real bundler; ESM restored; duplicate-export check | `formatBytes` bug dies; refactors become safe |
| **T1** | Token layers + `light-dark()`; literals migrated file by file behind the "0 literals" gate | dark mode arrives as a side effect |
| **T2** | Cascade layers; delete `!important`; three breakpoints | stylesheet stops fighting itself |
| **T3** | Modes as token overrides (§2.4); wire the orphaned density toggle | 91 mode rules → ~10 token lines |
| **T4** | Component extraction, one triple at a time, starting with `dd-table` | each extraction deletes from `sections.js` / `header.js` |
| **T5** | Gallery + fixture tests; retire source-string assertions | template layer becomes developable without a dump |
| **T6** | Delegated events; state store; URL-hash sync | lazy segments need no re-binding |
| **T7** | Chrome modules (shell/search/filters/toc); delete `report.ui.js` bucket | last of the leftover files |

T0 and T1 are independent of the format redesign and can land first.

---

## 11. Open questions

1. **Bundler choice and build integration.** esbuild is the obvious pick, but it puts Node in the
   build path for a project that is currently pure MSBuild. Acceptable, or should the bundle be
   pre-built and committed as a generated artifact?
2. **Do the reading modes change content or only presentation?** If they should change *what is
   shown by default* (§8.3), that decision belongs in the format doc's editorial contract, and this
   layer only styles it. If they stay presentational, they are pure token overrides.
3. **Is the gallery shipped or dev-only?** Dev-only is cheaper; shipping it inside the report gives
   support engineers a legend for every widget.
4. **Container queries** require no polyfill in current evergreen browsers, but reports are opened
   in whatever is on the machine — including corporate-locked browsers. Is there a floor to
   support?
