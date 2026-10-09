# Report Structure

Everything about DumpDetective's reporting layer: where it is going, and where it is now.

## The design baseline

Three documents. All are design proposals — **none of them describes what ships today**, except that vision § 19
steps M1 (claims) and M2 (entities) have shipped; [m1-claims-plan.md](m1-claims-plan.md) and
[m2-entities-plan.md](m2-entities-plan.md) record what they built.

| Doc | Defines |
|---|---|
| [ReportSystemVision.md](ReportSystemVision.md) | the from-scratch target: session model, entity index, observation store, claim graph, coverage, widget vocabulary, wire format, gates. Covers single-dump, multi-dump and future trace sources in one model |
| [ReportFormatCleanSlate.md](ReportFormatCleanSlate.md) | *what the report contains* — editorial contract, closed widget set, entity pivots, payload |
| [ReportTemplateCleanSlate.md](ReportTemplateCleanSlate.md) | *how it is built and styled* — design tokens, component library, bundler, CSS architecture, budgets |

Read the vision doc first for direction; the two clean-slate docs are the detailed, measured
critique of the current report and carry the migration phases. Where they disagree, the vision doc
states the disagreement explicitly in its §20.

## Implementation plans

| Doc | Covers |
|---|---|
| [m1-claims-plan.md](m1-claims-plan.md) | vision § 19 step M1 (claims), with Phase 8's sinks and unconditional `report.json` folded in. **Done 2026-10-06** |
| [m2c-entity-kinds-plan.md](m2c-entity-kinds-plan.md) | vision § 19 step M2c (more entity kinds): module, method, object and AppDomain pages. **Done 2026-10-09**; GC root, handle and memory segment not built, revisit with relations (M2b) |
| [m2-entities-plan.md](m2-entities-plan.md) | vision § 19 step M2 (entities): entity table, entity-typed columns, per-entity summary, type and thread page. **Done 2026-10-08**; relations (M2b) and more entity kinds (M2c) deferred |

## Where current-state truth lives

The baseline above deliberately describes target states. For what the report *actually does today*,
read code, or these two analyses — both written by reading the source rather than the specs:

| Doc | Covers |
|---|---|
| [report-information-architecture.md](report-information-architecture.md) | the findings plane — how diagnoses are grouped and surfaced |
| [report-cross-analyzer-data-plane.md](report-cross-analyzer-data-plane.md) | the data plane — many analyzers measuring the same entities, each publishing its own table |

Authoritative in code:

| Fact | Source |
|---|---|
| Section ids (`A1`…`H7`) and their domains | [SectionIdDomainMap.cs](../../src/DumpDetective.Reporting/Services/SectionIdDomainMap.cs) |
| Document schema and every serialized field | [AnalysisReportDocument.cs](../../src/DumpDetective.Reporting/Models/AnalysisReportDocument.cs) |
| Section slots and the `SectionBlock` union | [AnalyzerDetailSection.cs](../../src/DumpDetective.Reporting/Models/AnalyzerDetailSection.cs) |
| Published known-limitations list | `BuildAppendix` in [ReportSectionAssembler.cs](../../src/DumpDetective.Reporting/Services/ReportSectionAssembler.cs) |
| Schema version policy | [../schema-versioning.md](../schema-versioning.md) |

The render path is: CLI orchestration → `CanonicalReportDocumentFactory` builds the
`AnalysisReportDocument` → `ReportSerializer` projects domains, findings, appendix and correlation
events → a formatter renders `Text`, `Markdown`, `Html` or `Json` → `HtmlReportRenderer` inlines
CSS and JS from embedded template resources.

**The runtime source of truth for HTML assets is the embedded files under
`src/DumpDetective.Reporting/Templates/`, not `wwwroot/`.** Files under `wwwroot/` are snapshots and
references; editing them changes nothing at runtime. This trips people up regularly enough to be
worth stating here.

## Removed 2026-09-06

Thirteen superseded documents were deleted; they are in git history if needed.

From this folder — `ProfessionalTierReport.md`, `SingleDumpReportFormat.md`,
`SingleDumpReportFormat.v2.md`, `SingleDumpReportImplementationPlan.md`,
`SingleDumpReportImplementationPlan.v2.md`, `TrendReportBlueprint.md`, `TrendReportFormat.md`,
`TrendReportFormat.v2.md`, `TrendReportImplementationPlan.v2.md`,
`TrendReportImplementationPlan2.md`, `ReportHtmlComponentChecklist.v2.md`

From `docs/improvements/` — `report-design-system.md` and `report-display-vision.md`, both folded
into the three baseline docs. Everything in them was either already covered or has been merged:
the flat-first visual language and severity-token discipline into
[ReportTemplateCleanSlate.md §2](ReportTemplateCleanSlate.md#2-design-system) and
[ReportSystemVision.md §11](ReportSystemVision.md#11-visual-design-specification); their trend
visuals (regression banner, multi-series timeline, delta cards, snapshot swimlane, per-analyzer
timelines) into the `dd-series` / `dd-claim` widgets and the trend section of
[ReportFormatCleanSlate.md §5.3](ReportFormatCleanSlate.md#53-what-it-unlocks); the SVG-only,
no-external-chart-library constraint and the redacted-copy rule into ReportSystemVision §11.6 and
§12.3; the render path and the `Templates/`-not-`wwwroot/` note into this file.

Why, so it is not reconstructed by accident:

- **They specified a structure the baseline replaces.** Per-analyzer sections with hand-authored
  narrative, a separate trend document type, style v1/v2, and reading modes are each explicitly
  deleted by the three docs above.
- **They had drifted from the code and were not a reliable record of shipped behavior.** The
  `T0`–`T7` trend anchors they declare "stable" appear nowhere in the source; the analyzer coverage
  map listed `SegmentAnalyzer`, `RetentionAnalyzer` and `DependentHandleAnalyzer`, none of which
  exist, and marked as missing several features that have since shipped (exact dominator retained
  bytes, async state-machine state distribution, string type-ownership). Both code-derived docs
  above already declared these specs unreliable in their own headers.
- **Their report contract contradicted settled direction** — it required every ranking to be capped
  by explicit top-N. The project removed top-K/capped-sample patterns deliberately; the baseline
  keeps full data and discloses progressively.
- The four `*.v2.md` and implementation-plan documents tracked work against that superseded
  structure and were closed or abandoned.
