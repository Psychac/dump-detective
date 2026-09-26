# Known Issues

Open bugs and unreliable tests that are known but not yet fixed. Remove an entry when it's fixed;
add one when something is found but deliberately left for later.

## Open bugs

- **`--separate-json` never writes `report.json`.** Found 2026-09-26.
  - Cause: `ReportOutputWriter`'s pattern for the embedded `<script id="report-json">` has a stray
    space — `(['"]) (report-json)` — so it never matches, and the writer falls back to writing the
    HTML alone.
  - Fix: remove the space. Until then, the report JSON can be read from the HTML: it's the
    gzip-base64 content of `<script id="report-json" data-encoding="gzip-base64">`.
- **JIT hotspot insight can point at a framework module.** Found 2026-09-26.
  - `JitModuleHotspotInsightRule`'s first hit on the reference dump was `mscorlib.dll` — correct by
    the rule's definition, but not actionable.
  - The pre-migration `InsightEngine` rule never filtered framework modules either, so adding a
    filter is a new judgment call, not a regression fix.

- **15 cross-analyzer insights appear twice in the report.** Found 2026-09-26 (Phase 5
  pre-phase-vs-HEAD check).
  - Cause: `ReportDomainProjector` keeps a finding out of the domain sections only if it's tagged
    `cross-analyzer`, but puts every `InsightEngine` finding in the cross-domain list. Only 3 of the
    old engine's findings carried the tag, and none of the 38 migrated rules do.
  - Effect: they show in a domain section and in cross-domain insights. They are also counted
    twice as score contributors; the leak score is capped at 100 on the reference dump, so it
    doesn't show there.
  - Likely fix: skip `InsightEngine` findings in the domain projection as well.
- **Console insights are no longer sorted by severity.** Found 2026-09-26, same check.
  - The old engine emitted them in severity order. `SynthesisEngine` sorts all findings by
    fingerprint, so Critical/Warning/Info now interleave.

## Flaky tests

Each of these passes when run on its own but fails intermittently in full-suite runs. None is
caused by current work; treat a failure here as noise only after re-running the test alone.

| Test | Symptom | Notes |
|---|---|---|
| `RetainedSizeCandidateSelectorTests.SelectAndCompute_RespectsMaxCandidatesToWalk_RankedByShallowSizeDescending` | "Did not expect smallAddr to be 0UL" | ~1 in 10 runs; confirmed pre-existing — see [dominator-tree-memory-profile.md](analysis/phase1-redesigns/dominator-tree-memory-profile.md) |
| `TypeMetadataCacheFieldRecursionTests.ContainsPointers_TrueForKnownBclContainers_NoWhitelistNeeded` | Assertion failure | Seen twice, 2026-09-24/26 |
| `ArrayAnalyzerRetypingCharacterizationTests.AnalyzeAsync_RealSparseArray_ComputesExactNullOrZeroDensity` | Assertion failure; once **hung** when re-run alone | Seen 2026-09-24; the hung test host had to be killed |
| `GCHandleAnalyzerRetypingCharacterizationTests.AnalyzeAsync_NoIndex_MatchesClrMdGroundTruth` | Assertion failure | Seen 2026-09-26 |
| `BoundedGraphWalkTests.ComputeExclusiveRetained_PreCancelledToken_ThrowsOperationCanceled` | Assertion failure | Seen 2026-09-26 |

- **Common thread:** the first four take a snapshot of the test process itself
  (`DataTarget.CreateSnapshotAndAttach(Environment.ProcessId)`), so their input depends on whatever
  else the test host is doing at that moment. `BoundedGraphWalkTests` is the exception — a
  cancellation-timing test.
- **Not investigated yet.** Root-causing any one of these is open work.
