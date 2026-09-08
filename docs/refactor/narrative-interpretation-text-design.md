# Shared narrative interpretation text — design

## Motivation

Seed idea: [dominator-analyzer-audit.md](../analysis/phase1/dominator-analyzer-audit.md)
P2 row — "Add narrative interpretation text for the retained/shallow ratio (e.g. 'retained
>> shallow → holds an external graph' vs. 'retained ≈ shallow → self-contained') next to the
Gen2/LOH sub-table." Not yet implemented.

That gap isn't specific to Dominator. Many section builders surface a bare `MetricUnit.Ratio`
or `MetricUnit.Percent` value with no read on what the number means — an engineer sees
`duplication_ratio: 3.2` or `dead_target_ratio: 41%` and has to already know the domain to
know whether that's fine or alarming. Confirmed candidates today (ratio/percent metrics with
no accompanying interpretation):

- `DominatorSectionBuilder` — retained/shallow ratio (the seed case, `RatioValue` helper
  already computed, just not narrated).
- `StringSectionBuilder` — `duplication_ratio` (`StringSectionBuilder.cs:59`).
- `WeakReferenceSectionBuilder` — `dead_target_ratio` (`WeakReferenceSectionBuilder.cs:31`).
- `LohFragmentationSectionBuilder` — `overall_fragmentation_pct` (`LohFragmentationSectionBuilder.cs:36`).
- `GCPressureSectionBuilder` — per-type Gen2 survival ratio column.

Other builders emitting `MetricUnit.Ratio`/`MetricUnit.Percent` (`AllocationPatternSectionBuilder`,
`BoxingSectionBuilder`, `GCHandleSectionBuilder`, `HangSectionBuilder`,
`HeapTopologySectionBuilder`, `JitSectionBuilder`, `MemoryAnalysisSectionBuilder`,
`ReferenceChainSectionBuilder`, `SegmentReservationSectionBuilder`, `StaticRootSectionBuilder`,
`ThreadStackClusterSectionBuilder`) are plausible future adopters but not individually
scoped — pick them up opportunistically, same rule as the tree widget below.

Rather than let each analyzer invent its own ad hoc "if ratio > X, append a TextBlock" pattern,
build one small shared convention now, model it on the retained/shallow case, and adopt
elsewhere only where a specific ratio's meaning genuinely isn't self-evident from the label.

## Shape

New block type (distinct from `TextBlock`) so renderers can style it consistently (e.g. a
muted "aside" tone, an interpretation glyph) instead of blending into ordinary narrative prose:

```csharp
internal sealed record InterpretationBlock(string Text, int IndentLevel = 0) : SectionBlock;
```

Shared helper in `SectionBuilderBase`, next to `BuildConfidenceBand`:

```csharp
protected static InterpretationBlock? Interpret(double? value, params (double Threshold, string Text)[] tiers)
```

- `tiers` ordered highest-threshold-first; returns the first tier whose `Threshold` the value
  meets or exceeds, else the last (lowest) tier as the floor case. Returns `null` when
  `value` is `null` — no interpretation for missing data, don't fabricate one.
- Thresholds and wording stay caller-owned (each analyzer knows its own domain semantics) —
  the helper only standardizes tier-selection and rendering, not the text itself.

Example call site (Dominator retained/shallow ratio):

```csharp
blocks.Add(Interpret(ratio,
    (3.0, "retained ≫ shallow → holds a large external graph"),
    (1.2, "retained > shallow → holds some external references"),
    (0.0, "retained ≈ shallow → largely self-contained")));
```

## Rendering approach

Add `InterpretationBlock` alongside the other `[JsonDerivedType]` entries on `SectionBlock`
(`AnalyzerDetailSection.cs`), and a render case in each formatter (`RenderBlocksMd`,
`RenderBlocksHtml`, `RenderBlocksHtml` in `ReportHtmlShared.cs`) — same mechanical addition
`ConfidenceBandBlock` already went through, no new interaction model needed since this is
static text, not collapsible/interactive like the tree widget.

## Testing

Unit-test `SectionBuilderBase.Interpret` directly (tier selection, `null` passthrough,
boundary-equals-threshold behavior) — cheap, pure function, no need to round-trip through a
real analyzer. Per-adopter tests just assert the right `InterpretationBlock` text appears for
a given input value, same pattern as existing `*SectionBuilderTests`.

## Adoption tracking

1. **✅ DONE 2026-09-03 — Dominator retained/shallow ratio**, first consumer, drives initial
   helper shape. Closes the audit's original P2 item. Shipped as designed:
   `InterpretationBlock` (`AnalyzerDetailSection.cs`) + `SectionBuilderBase.Interpret`, render
   cases added to all three formatters (Markdown, HTML, plain text — one more than the design
   sketch's "each formatter" originally enumerated, since the plain-text formatter is a real
   third output mode in this codebase). `DominatorSectionBuilder` interprets the *top-ranked*
   Gen2/LOH candidate's ratio (the sub-table is already sorted by Gen2Count/LohBytes
   descending) rather than every row, naming the type in the text so the blurb stays
   unambiguous about which of the table's several rows it describes — a scoping decision the
   original sketch's single-value example didn't need to make, since it assumed one ratio, not
   a table of them. Tests: `SectionBuilderBaseInterpretTests` (tier selection, null/empty-tiers,
   boundary-inclusive, floor fallthrough), `DominatorSectionBuilderTests` (all three tiers via
   the top candidate).
2. `StringSectionBuilder` duplication ratio, `WeakReferenceSectionBuilder` dead-target ratio,
   `LohFragmentationSectionBuilder` fragmentation pct, `GCPressureSectionBuilder` survival
   ratio — not yet adopted; the helper shape held up for item 1 without needing changes, so
   these remain buildable whenever picked up, still deliberately deferred rather than bundled
   into this change.
3. Remaining `MetricUnit.Ratio`/`MetricUnit.Percent` emitters — no fixed follow-up item yet;
   add interpretation only where a reviewer flags a specific number as ambiguous, not
   blanket-applied to every ratio in the report.

Do not force items 2–3 into the same change as item 1 — land the helper against the first
real consumer, confirm the tier-threshold shape holds up, then adopt elsewhere.
